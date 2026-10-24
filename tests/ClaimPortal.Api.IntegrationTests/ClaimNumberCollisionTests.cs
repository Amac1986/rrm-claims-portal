using System.Net;
using System.Net.Http.Json;
using ClaimPortal.Contracts.Api;
using ClaimPortal.Domain.Claims;
using ClaimPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ClaimPortal.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class ClaimNumberCollisionTests(ClaimPortalStack stack)
{
    private static readonly DateOnly SubmissionDate = DateOnly.FromDateTime(TestClaims.SubmissionTime.UtcDateTime);
    private static readonly ClaimNumber TakenNumber = ClaimNumber.FromParts(SubmissionDate, "AAAAAA");
    private static readonly ClaimNumber RetryNumber = ClaimNumber.FromParts(SubmissionDate, "BBBBBB");

    [Fact]
    public async Task The_repository_translates_the_database_unique_violation_for_a_retryable_collision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(stack, cancellationToken);

        await using (var setup = ClaimsDatabase.CreateContext(database.ConnectionString))
        {
            await setup.Database.MigrateAsync(cancellationToken);
        }

        await using (var first = ClaimsDatabase.CreateContext(database.ConnectionString))
        {
            await new ClaimRepository(first).AddAsync(NewClaim(TakenNumber), cancellationToken);
        }

        await using (var second = ClaimsDatabase.CreateContext(database.ConnectionString))
        {
            var collision = await Should.ThrowAsync<ClaimNumberCollisionException>(
                () => new ClaimRepository(second).AddAsync(NewClaim(TakenNumber), cancellationToken));

            collision.ClaimNumber.ShouldBe(TakenNumber);
        }

        (await ClaimsDatabase.CountAsync(database.ConnectionString, "claims", cancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_collision_is_retried_against_the_real_constraint_and_only_the_committed_attempt_is_published()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(stack, cancellationToken);

        await using var probe = await ClaimSubmittedProbe.StartAsync(stack, cancellationToken);
        await using var factory = new ClaimsApiFactory(
            stack,
            database.ConnectionString,
            // First submission takes AAAAAA. The second submission's first attempt collides
            // on AAAAAA and must be rolled back, then succeed with BBBBBB.
            TestClaims.WithClaimNumbers(TakenNumber, TakenNumber, RetryNumber));

        using var client = factory.CreateClient();

        var first = await SubmitAsync(client, cancellationToken);
        first.ClaimNumber.ShouldBe(TakenNumber.Value);

        var second = await SubmitAsync(client, cancellationToken);
        second.ClaimNumber.ShouldBe(RetryNumber.Value);

        var published = new List<ConsumedClaim>
        {
            await probe.NextAsync(TimeSpan.FromSeconds(30), cancellationToken),
            await probe.NextAsync(TimeSpan.FromSeconds(30), cancellationToken),
        };

        // If the failed attempt's outbox row had survived its rollback, AAAAAA would be
        // published twice. Exactly one event per committed claim is the assertion.
        published.Select(claim => claim.Message.ClaimNumber)
            .ShouldBe(new[] { TakenNumber.Value, RetryNumber.Value }, ignoreOrder: true);

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        probe.TryRead(out _).ShouldBeFalse(
            "the rolled-back attempt must not leave a deliverable outbox message behind");

        await using var db = ClaimsDatabase.CreateContext(database.ConnectionString);
        var numbers = await db.Claims
            .AsNoTracking()
            .Select(claim => claim.ClaimNumber.Value)
            .ToListAsync(cancellationToken);

        numbers.ShouldBe(new[] { TakenNumber.Value, RetryNumber.Value }, ignoreOrder: true);
    }

    [Fact]
    public async Task Exhausting_the_retry_budget_leaves_no_claim_and_no_event()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(stack, cancellationToken);

        await using (var seed = ClaimsDatabase.CreateContext(database.ConnectionString))
        {
            await seed.Database.MigrateAsync(cancellationToken);
            await new ClaimRepository(seed).AddAsync(NewClaim(TakenNumber), cancellationToken);
        }

        await using var probe = await ClaimSubmittedProbe.StartAsync(stack, cancellationToken);
        await using var factory = new ClaimsApiFactory(
            stack,
            database.ConnectionString,
            TestClaims.WithClaimNumbers(TakenNumber, TakenNumber, TakenNumber));

        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/claims",
            JsonContent.Create(TestClaims.ValidRequest(), options: TestClaims.JsonOptions),
            cancellationToken);

        // Three collisions exhaust BoundedClaimNumberRetryPolicy and surface as the API's
        // unhandled-error path, not as a leaked exception or a phantom 201.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        var problem = await response.Content.ReadAsStringAsync(cancellationToken);
        problem.ShouldNotContain(nameof(ClaimNumberCollisionException));

        using var document = System.Text.Json.JsonDocument.Parse(problem);
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
        document.RootElement.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();

        // The contract's 500 shape: ProblemDetails with no `errors` member and no
        // exception detail.
        document.RootElement.TryGetProperty("errors", out _).ShouldBeFalse();

        // The published rows were written inside each attempt's transaction, so none of
        // them can be committed once every attempt rolls back.
        (await ClaimsDatabase.CountAsync(database.ConnectionString, "claims", cancellationToken))
            .ShouldBe(1);
        (await ClaimsDatabase.CountAsync(database.ConnectionString, "outbox_message", cancellationToken))
            .ShouldBe(0);

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        probe.TryRead(out _).ShouldBeFalse("a failed claim must not publish ClaimSubmitted");
    }

    private static Claim NewClaim(ClaimNumber claimNumber) => Claim.Create(
        claimNumber,
        "HO-4471902",
        new Claimant("Dana", "Okonjo", "dana.okonjo@example.ca", "204-555-0100"),
        SubmissionDate.AddDays(-1),
        ClaimPortal.Domain.Claims.LossType.Water,
        "The basement flooded after a pipe burst.",
        12500m,
        new ClaimPortal.Domain.Claims.PropertyAddress(
            "12 Main Street",
            null,
            "Winnipeg",
            ProvinceCode.Parse("MB"),
            PostalCode.Parse("R3C 1A1")),
        TestClaims.SubmissionTime);

    private static async Task<SubmitClaimResponse> SubmitAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(
            "/api/claims",
            JsonContent.Create(TestClaims.ValidRequest(), options: TestClaims.JsonOptions),
            cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubmitClaimResponse>(
            TestClaims.JsonOptions,
            cancellationToken);

        body.ShouldNotBeNull();
        return body!;
    }
}

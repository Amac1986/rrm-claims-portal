using System.Net;
using System.Net.Http.Json;
using ClaimPortal.Contracts.Api;
using ClaimPortal.Domain.Claims;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ClaimPortal.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class SubmitClaimEndToEndTests(ClaimPortalStack stack)
{
    [Fact]
    public async Task A_valid_claim_lands_in_postgres_and_returns_the_contract_shape()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(stack, cancellationToken);

        await using var factory = new ClaimsApiFactory(stack, database.ConnectionString);
        using var client = factory.CreateClient();

        var request = TestClaims.ValidRequest();

        using var response = await client.PostAsync(
            "/api/claims",
            JsonContent.Create(request, options: TestClaims.JsonOptions),
            cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubmitClaimResponse>(
            TestClaims.JsonOptions,
            cancellationToken);

        body.ShouldNotBeNull();
        body!.Status.ShouldBe("Received");
        body.ClaimNumber.ShouldMatch(@"^CLM-\d{8}-[0-9A-HJKMNP-TV-Z]{6}$");
        body.SubmittedAt.Offset.ShouldBe(TimeSpan.Zero);

        response.Headers.Location.ShouldNotBeNull();
        var location = response.Headers.Location!;
        var locationPath = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        locationPath.ShouldBe($"/api/claims/{body.ClaimId}");

        await using var db = ClaimsDatabase.CreateContext(database.ConnectionString);
        var row = await db.Claims
            .AsNoTracking()
            .SingleAsync(claim => claim.Id == body.ClaimId, cancellationToken);

        row.ClaimNumber.Value.ShouldBe(body.ClaimNumber);
        row.PolicyNumber.ShouldBe("HO-4471902");
        row.Claimant.FirstName.ShouldBe("Dana");
        row.Claimant.LastName.ShouldBe("Okonjo");
        row.Claimant.Email.ShouldBe("dana.okonjo@example.ca");
        row.IncidentDate.ShouldBe(request.IncidentDate!.Value);
        row.LossType.ShouldBe(ClaimPortal.Domain.Claims.LossType.Water);
        row.Status.ShouldBe(ClaimStatus.Received);
        row.PropertyAddress.PostalCode.Value.ShouldBe("R3C 1A1");

        // timestamptz is microsecond-precision; the response carries the clock's ticks.
        Math.Abs((row.SubmittedAt - body.SubmittedAt).TotalMilliseconds).ShouldBeLessThan(1);

        // The API's startup migrator, not a test-only EnsureCreated, built this schema.
        (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ShouldContain("20261024015554_InitialClaims");
    }

    [Fact]
    public async Task Two_submissions_get_distinct_claim_numbers_and_both_rows_land()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(stack, cancellationToken);

        await using var factory = new ClaimsApiFactory(stack, database.ConnectionString);
        using var client = factory.CreateClient();

        var first = await SubmitAsync(client, TestClaims.ValidRequest(), cancellationToken);
        var second = await SubmitAsync(client, TestClaims.ValidRequest("HO-9988776"), cancellationToken);

        first.ClaimNumber.ShouldNotBe(second.ClaimNumber);
        first.ClaimId.ShouldNotBe(second.ClaimId);

        await using var db = ClaimsDatabase.CreateContext(database.ConnectionString);
        var rows = await db.Claims.AsNoTracking().ToListAsync(cancellationToken);

        rows.Count.ShouldBe(2);
        rows.Select(row => row.ClaimNumber.Value)
            .ShouldBe(new[] { first.ClaimNumber, second.ClaimNumber }, ignoreOrder: true);
    }

    private static async Task<SubmitClaimResponse> SubmitAsync(
        HttpClient client,
        SubmitClaimRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(
            "/api/claims",
            JsonContent.Create(request, options: TestClaims.JsonOptions),
            cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubmitClaimResponse>(
            TestClaims.JsonOptions,
            cancellationToken);

        body.ShouldNotBeNull();
        return body!;
    }
}

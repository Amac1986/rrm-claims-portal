using System.Net;
using System.Net.Http.Json;
using ClaimPortal.Contracts.Api;
using Shouldly;
using Xunit;

namespace ClaimPortal.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class OutboxPublishingTests(ClaimPortalStack stack)
{
    // The outbox dispatcher polls every second (Program.cs sets QueryDelay); a generous
    // ceiling keeps the assertion about behaviour, not about a slow CI box.
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_outbox_publishes_ClaimSubmitted_to_a_real_broker_after_commit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(stack, cancellationToken);

        // Subscribe before submitting: the queue must be bound to the exchange before the
        // outbox dispatcher can publish, or the assertion would race the topology.
        await using var probe = await ClaimSubmittedProbe.StartAsync(stack, cancellationToken);
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

        var consumed = await probe.NextAsync(DeliveryTimeout, cancellationToken);

        consumed.MessageId.ShouldNotBeNull();
        consumed.Message.ClaimId.ShouldBe(body!.ClaimId);
        consumed.Message.ClaimNumber.ShouldBe(body.ClaimNumber);
        consumed.Message.PolicyNumber.ShouldBe(request.PolicyNumber);
        consumed.Message.ClaimantFirstName.ShouldBe(request.FirstName);
        consumed.Message.ClaimantLastName.ShouldBe(request.LastName);
        consumed.Message.ClaimantEmail.ShouldBe(request.Email);
        consumed.Message.LossType.ShouldBe(ClaimPortal.Contracts.LossType.Water);
        consumed.Message.IncidentDate.ShouldBe(request.IncidentDate!.Value);
        consumed.Message.SubmittedAt.ShouldBe(body.SubmittedAt);

        // The broker event is the same claim that landed in the database, not a message
        // written by a second, independent code path.
        await using var db = ClaimsDatabase.CreateContext(database.ConnectionString);
        var row = await db.Claims.FindAsync([body.ClaimId], cancellationToken);
        row.ShouldNotBeNull();
        row.ClaimNumber.Value.ShouldBe(consumed.Message.ClaimNumber);
    }
}

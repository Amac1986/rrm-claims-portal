using ClaimPortal.Api.Messaging;
using ClaimPortal.Contracts;
using ClaimPortal.Contracts.Api;
using ClaimPortal.Domain.Claims;
using ClaimPortal.Observability;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using DomainLossType = ClaimPortal.Domain.Claims.LossType;
using DomainPropertyAddress = ClaimPortal.Domain.Claims.PropertyAddress;

namespace ClaimPortal.Api;

/// <summary>
/// `POST /api/claims`. The only public write endpoint in the slice.
/// </summary>
internal static class ClaimsEndpoints
{
    public static IEndpointRouteBuilder MapClaimsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/claims", SubmitClaim);
        return app;
    }

    private static async Task<IResult> SubmitClaim(
        SubmitClaimRequest request,
        IValidator<SubmitClaimRequest> validator,
        IClaimNumberRetryPolicy retryPolicy,
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ClaimPortal.Api.Claims");

        // Established by CorrelationIdMiddleware. This exact string is written into the
        // MassTransit header below, so the API log, broker message and worker log line up.
        var correlationId = CorrelationContext.Current
            ?? CorrelationId.Resolve(null, System.Diagnostics.Activity.Current);

        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            var errors = validation.Errors
                .GroupBy(failure => failure.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(failure => failure.ErrorMessage).ToArray());

            return Results.ValidationProblem(errors);
        }

        // One clock reading for the claim number, the persisted timestamp and the event.
        var submittedAt = clock.GetUtcNow();

        var claim = await retryPolicy.ExecuteAsync(
            async (_, attemptCancellation) =>
            {
                // A fresh scope per attempt is required by IClaimNumberRetryPolicy: retrying in
                // the same DbContext would leave the rejected attempt's outbox message tracked
                // as Added and could commit two ClaimSubmitted rows.
                await using var scope = scopeFactory.CreateAsyncScope();

                var generator = scope.ServiceProvider.GetRequiredService<IClaimNumberGenerator>();
                var repository = scope.ServiceProvider.GetRequiredService<IClaimRepository>();
                var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

                var candidate = Map(request, generator.Generate(submittedAt), submittedAt);

                // Publish before AddAsync. With UseBusOutbox this writes an outbox row into the
                // same DbContext; AddAsync's SaveChanges commits the claim and the message in
                // one transaction. The correlation id rides in the message header, which is
                // what lets the worker continue the same story in its own log.
                await publisher.Publish(
                    ToEvent(candidate),
                    publish => publish.Headers.Set(CorrelationId.HeaderName, correlationId),
                    attemptCancellation);
                await repository.AddAsync(candidate, attemptCancellation);

                return candidate;
            },
            cancellationToken);

        logger.LogInformation(
            "Accepted claim {ClaimNumber} ({ClaimId}) for loss type {LossType}.",
            claim.ClaimNumber.Value,
            claim.Id,
            claim.LossType);

        var response = new SubmitClaimResponse
        {
            ClaimId = claim.Id,
            ClaimNumber = claim.ClaimNumber.Value,
            SubmittedAt = claim.SubmittedAt,
            Status = claim.Status.ToString(),
        };

        return Results.Created($"/api/claims/{claim.Id}", response);
    }

    private static Claim Map(SubmitClaimRequest request, ClaimNumber claimNumber, DateTimeOffset submittedAt)
    {
        var address = request.PropertyAddress!;

        return Claim.Create(
            claimNumber,
            request.PolicyNumber!,
            new Claimant(request.FirstName!, request.LastName!, request.Email!, request.Phone!),
            request.IncidentDate!.Value,
            Enum.Parse<DomainLossType>(request.LossType!.Value.ToString(), ignoreCase: false),
            request.Description!,
            request.EstimatedLossAmount,
            new DomainPropertyAddress(
                address.AddressLine1!,
                address.AddressLine2,
                address.City!,
                ProvinceCode.Parse(address.Province),
                PostalCode.Parse(address.PostalCode)),
            submittedAt);
    }

    private static ClaimSubmitted ToEvent(Claim claim) => new()
    {
        ClaimId = claim.Id,
        ClaimNumber = claim.ClaimNumber.Value,
        PolicyNumber = claim.PolicyNumber,
        ClaimantFirstName = claim.Claimant.FirstName,
        ClaimantLastName = claim.Claimant.LastName,
        ClaimantEmail = claim.Claimant.Email,
        LossType = Enum.Parse<ClaimPortal.Contracts.LossType>(claim.LossType.ToString(), ignoreCase: false),
        IncidentDate = claim.IncidentDate,
        SubmittedAt = claim.SubmittedAt,
    };
}

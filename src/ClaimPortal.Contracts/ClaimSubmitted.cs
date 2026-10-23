namespace ClaimPortal.Contracts;

/// <summary>
/// Published by the Claims API through the transactional outbox once a claim has been
/// persisted, and consumed asynchronously by ClaimPortal.Notifications to send the
/// customer's confirmation email.
/// </summary>
/// <remarks>
/// <para>
/// This event uses event-carried state transfer: it carries everything the consumer
/// needs to render the confirmation email, so the consumer never calls back into the
/// API or reads the claims table. The two services stay independent, and the email
/// reflects the claim exactly as it was submitted.
/// </para>
/// <para>
/// Loss details (description, estimated amount, property address) are deliberately
/// excluded. The confirmation email does not include them, and keeping them out of a
/// message payload that ends up in an email pipeline is the safer default.
/// </para>
/// <para>
/// Idempotency: the transport <c>MessageId</c> is the outbox row id and is stable
/// across redeliveries. Consumers must treat it as the deduplication key rather than
/// deriving one from this payload.
/// </para>
/// <para>
/// This contract is frozen. See <c>docs/CONTRACT.md</c>.
/// </para>
/// </remarks>
public sealed record ClaimSubmitted
{
    /// <summary>Primary key of the persisted claim.</summary>
    public required Guid ClaimId { get; init; }

    /// <summary>Customer-facing claim number, e.g. <c>CLM-20261026-K7F3QX</c>.</summary>
    public required string ClaimNumber { get; init; }

    /// <summary>Policy the claim was filed against.</summary>
    public required string PolicyNumber { get; init; }

    public required string ClaimantFirstName { get; init; }

    public required string ClaimantLastName { get; init; }

    /// <summary>Destination address for the confirmation email.</summary>
    public required string ClaimantEmail { get; init; }

    public required LossType LossType { get; init; }

    /// <summary>Date of the incident. A calendar date, not a timestamp.</summary>
    public required DateOnly IncidentDate { get; init; }

    /// <summary>When the claim was accepted by the API. UTC.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }
}

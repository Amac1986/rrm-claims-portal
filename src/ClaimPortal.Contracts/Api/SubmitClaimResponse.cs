namespace ClaimPortal.Contracts.Api;

/// <summary>
/// <c>201 Created</c> body for <c>POST /api/claims</c>.
/// This contract is frozen — see <c>docs/CONTRACT.md</c>.
/// </summary>
public sealed record SubmitClaimResponse
{
    public required Guid ClaimId { get; init; }

    /// <summary>
    /// Customer-facing claim number the confirmation screen displays and the
    /// confirmation email repeats, e.g. <c>CLM-20261026-K7F3QX</c>.
    /// </summary>
    public required string ClaimNumber { get; init; }

    /// <summary>When the API accepted the claim. UTC.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>
    /// Always <c>"Received"</c> in this slice. Present so the client is not
    /// hard-coding a lifecycle state that will grow later.
    /// </summary>
    public required string Status { get; init; }
}

namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Lifecycle state of a claim.
/// </summary>
/// <remarks>
/// Only <see cref="Received"/> exists in this slice — the contract pins the response
/// <c>status</c> to the literal <c>"Received"</c>. It is an enum rather than a constant
/// string so the states this will grow (triage, adjusting, settled) have somewhere to go,
/// and so the column is a closed set rather than free text.
/// </remarks>
public enum ClaimStatus
{
    /// <summary>The claim has been accepted and persisted; nothing has been assessed yet.</summary>
    Received,
}

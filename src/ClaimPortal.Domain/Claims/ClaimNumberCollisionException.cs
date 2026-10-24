namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Thrown when the claims store rejects a claim because its claim number is already
/// taken. Expected and retryable — the caller generates a new number and tries again.
/// </summary>
/// <remarks>
/// Lives in the domain, not in Infrastructure, so the retry policy and the endpoint can
/// catch it without either of them referencing Npgsql. Infrastructure is the only thing
/// that knows this is really a Postgres <c>23505</c> on <c>ix_claims_claim_number</c>.
/// </remarks>
public sealed class ClaimNumberCollisionException : Exception
{
    /// <summary>Creates the exception for a specific rejected number.</summary>
    public ClaimNumberCollisionException(ClaimNumber claimNumber, Exception? innerException = null)
        : base($"Claim number '{claimNumber.Value}' is already in use.", innerException)
        => ClaimNumber = claimNumber;

    /// <summary>The claim number the database rejected.</summary>
    public ClaimNumber ClaimNumber { get; }
}

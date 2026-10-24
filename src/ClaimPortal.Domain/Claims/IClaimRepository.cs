namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Persistence port for claims. Implemented in ClaimPortal.Infrastructure.
/// </summary>
public interface IClaimRepository
{
    /// <summary>
    /// Inserts the claim and commits the unit of work.
    /// </summary>
    /// <remarks>
    /// This commits <em>everything</em> staged on the current unit of work, not just the
    /// claim row — that is the point. The transactional outbox works because the claim
    /// and the <c>ClaimSubmitted</c> outbox row land in the same database transaction, so
    /// callers must stage the publish <em>before</em> calling this.
    /// </remarks>
    /// <exception cref="ClaimNumberCollisionException">
    /// The claim number is already taken. Reissue it with
    /// <see cref="Claim.ReissueClaimNumber"/> and retry — see
    /// <see cref="IClaimNumberRetryPolicy"/>.
    /// </exception>
    Task AddAsync(Claim claim, CancellationToken cancellationToken = default);
}

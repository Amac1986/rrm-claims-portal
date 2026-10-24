namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Produces candidate claim numbers. "Candidate" is the operative word: the generator
/// makes collisions unlikely, the database unique index makes them impossible, and the
/// caller retries a bounded number of times when the index says no.
/// </summary>
public interface IClaimNumberGenerator
{
    /// <summary>
    /// Generates a claim number whose date segment is the UTC date of
    /// <paramref name="submittedAt"/>.
    /// </summary>
    /// <remarks>
    /// The submission timestamp is a parameter rather than something the generator reads
    /// from a clock so that the number, the persisted <c>submitted_at</c> and the
    /// <c>submittedAt</c> on the response and the event all come from one reading. Under a
    /// retry, or across midnight UTC, a generator with its own clock could hand back a
    /// number dated a day away from the claim it belongs to.
    /// </remarks>
    ClaimNumber Generate(DateTimeOffset submittedAt);
}

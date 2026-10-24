using System.Security.Cryptography;

namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Default <see cref="IClaimNumberGenerator"/>: a CSPRNG-sourced six-character Crockford
/// base32 suffix behind the UTC submission date.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RandomNumberGenerator.GetItems{T}(ReadOnlySpan{T}, int)"/> does the sampling.
/// It is cryptographically sourced and, unlike the obvious <c>bytes[i] % 32</c>, it is
/// free of modulo bias. Here 256 happens to be a multiple of 32 so the bias would have
/// been zero anyway — but that is an accident of the alphabet length, and it would
/// silently stop being true the day someone adds a character.
/// </para>
/// <para>
/// Rejected alternative: a database sequence, which would make the number unique by
/// construction and remove the retry entirely. It also makes the number guessable and
/// leaks the company's claim volume to anyone holding two confirmation emails, which is
/// exactly the enumeration problem a random suffix avoids. Rejected alternative:
/// <c>Random.Shared</c>, which is fast and seeded well but is not a CSPRNG and is
/// therefore the wrong tool for an identifier people can try to guess.
/// </para>
/// <para>
/// The keyspace is 32^6 ≈ 1.07 billion per day. At a thousand claims a day the chance of
/// even one same-day collision is about 1 in 2,000 — rare, handled by the unique index
/// and the retry, and never relied on for correctness.
/// </para>
/// </remarks>
public sealed class CrockfordClaimNumberGenerator : IClaimNumberGenerator
{
    /// <inheritdoc />
    public ClaimNumber Generate(DateTimeOffset submittedAt)
    {
        var suffix = RandomNumberGenerator.GetItems<char>(ClaimNumber.Alphabet, ClaimNumber.SuffixLength);
        var submittedOnUtc = DateOnly.FromDateTime(submittedAt.UtcDateTime);

        return ClaimNumber.FromParts(submittedOnUtc, new string(suffix));
    }
}

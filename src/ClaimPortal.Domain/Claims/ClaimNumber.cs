using System.Diagnostics.CodeAnalysis;

namespace ClaimPortal.Domain.Claims;

/// <summary>
/// A customer-facing claim number: <c>CLM-{yyyyMMdd}-{6 chars Crockford base32}</c>,
/// e.g. <c>CLM-20261026-K7F3QX</c>.
/// </summary>
/// <remarks>
/// This is not the primary key — <c>Claim.Id</c> is. The claim number is the identifier a
/// customer reads out over the phone, which is why it is short, date-prefixed and uses an
/// alphabet with no visually ambiguous characters.
/// </remarks>
public readonly record struct ClaimNumber
{
    /// <summary>Fixed prefix on every claim number.</summary>
    public const string Prefix = "CLM-";

    /// <summary>Number of random characters in the suffix.</summary>
    public const int SuffixLength = 6;

    /// <summary>Total length of a claim number: <c>CLM-</c> + 8 date digits + <c>-</c> + 6.</summary>
    public const int TotalLength = 4 + 8 + 1 + SuffixLength;

    /// <summary>
    /// Crockford base32: the digits plus the uppercase letters, excluding I, L, O and U.
    /// I/L/O are excluded because they are easily confused with 1 and 0 when a customer
    /// reads a claim number off a confirmation email; U is excluded by Crockford so that
    /// an accidentally generated obscenity is less likely.
    /// </summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private ClaimNumber(string value) => Value = value;

    /// <summary>The claim number as it appears to the customer.</summary>
    public string Value { get; }

    /// <summary>
    /// Builds a claim number from a submission date and an already-generated suffix.
    /// Prefer <see cref="IClaimNumberGenerator"/>; this exists for the generator and for tests.
    /// </summary>
    /// <exception cref="ArgumentException">The suffix is not six characters from <see cref="Alphabet"/>.</exception>
    public static ClaimNumber FromParts(DateOnly submittedOnUtc, string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);

        if (suffix.Length != SuffixLength || !IsAlphabet(suffix))
        {
            throw new ArgumentException(
                $"Suffix must be {SuffixLength} characters from the Crockford base32 alphabet.",
                nameof(suffix));
        }

        return new ClaimNumber($"{Prefix}{submittedOnUtc:yyyyMMdd}-{suffix}");
    }

    /// <summary>Validates and wraps an existing claim number string.</summary>
    public static bool TryParse(string? value, out ClaimNumber claimNumber)
    {
        claimNumber = default;

        if (value is null || value.Length != TotalLength)
        {
            return false;
        }

        if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value[12] != '-')
        {
            return false;
        }

        for (var i = 4; i < 12; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        if (!DateOnly.TryParseExact(
                value.AsSpan(4, 8),
                "yyyyMMdd",
                null,
                System.Globalization.DateTimeStyles.None,
                out _))
        {
            return false;
        }

        if (!IsAlphabet(value.AsSpan(13)))
        {
            return false;
        }

        claimNumber = new ClaimNumber(value);
        return true;
    }

    /// <summary>Validates and wraps an existing claim number string.</summary>
    /// <exception cref="ArgumentException">The value is not a well-formed claim number.</exception>
    public static ClaimNumber Parse(string? value)
    {
        if (!TryParse(value, out var claimNumber))
        {
            throw new ArgumentException($"'{value}' is not a well-formed claim number.", nameof(value));
        }

        return claimNumber;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsAlphabet(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

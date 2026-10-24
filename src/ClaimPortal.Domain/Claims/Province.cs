using System.Diagnostics.CodeAnalysis;

namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Canadian province or territory, by its two-letter postal abbreviation.
/// The member names are the codes from <c>docs/CONTRACT.md</c> §4.
/// </summary>
public enum Province
{
    AB,
    BC,
    MB,
    NB,
    NL,
    NS,
    NT,
    NU,
    ON,
    PE,
    QC,
    SK,
    YT,
}

/// <summary>Parsing helpers for <see cref="Province"/>.</summary>
public static class ProvinceCode
{
    /// <summary>Every valid code, uppercase, in contract order.</summary>
    public static IReadOnlyList<string> All { get; } = Enum.GetNames<Province>();

    /// <summary>
    /// Parses a two-letter province or territory code.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on the way in, canonical uppercase on the way out. The contract
    /// describes <c>province</c> as uppercase; that is the canonical stored and returned
    /// form, not a reason to reject <c>"mb"</c> from a client that lower-cases its form
    /// state. Rejected alternative: exact-match uppercase only — stricter, but it turns a
    /// cosmetic client bug into a validation error the customer cannot act on.
    /// </remarks>
    public static bool TryParse(string? value, out Province province)
    {
        province = default;

        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();

        // Enum.TryParse would also accept "3" and "Province.MB"-ish input; the length
        // check plus letters-only keeps this to the 13 codes the contract lists.
        if (trimmed.Length != 2 || !char.IsAsciiLetter(trimmed[0]) || !char.IsAsciiLetter(trimmed[1]))
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out province);
    }

    /// <summary>Parses a code, throwing when it is not one of the 13 valid values.</summary>
    /// <exception cref="ArgumentException">The value is not a Canadian province or territory code.</exception>
    public static Province Parse(string? value)
    {
        if (!TryParse(value, out var province))
        {
            throw new ArgumentException(
                $"'{value}' is not a Canadian province or territory code.",
                nameof(value));
        }

        return province;
    }
}

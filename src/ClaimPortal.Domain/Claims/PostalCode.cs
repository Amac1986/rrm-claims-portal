using System.Diagnostics.CodeAnalysis;

namespace ClaimPortal.Domain.Claims;

/// <summary>
/// A Canadian postal code, normalized to the canonical <c>A1A 1A1</c> form:
/// uppercase, exactly one space between the forward sortation area and the local
/// delivery unit.
/// </summary>
/// <remarks>
/// <para>
/// Normalization happens here, at construction, rather than in the FluentValidation
/// validator. A validator's job is to answer yes/no; if it also rewrote the request it
/// would be the only place in the stack that could produce a normalized value, and any
/// caller that skipped it would persist <c>"k1a0b1"</c>. Putting it in the value object
/// means an unnormalized <see cref="PostalCode"/> cannot exist.
/// </para>
/// <para>
/// Validity here is the contract's rule — the <c>A1A 1A1</c> shape, letters and digits
/// alternating. Rejected alternative: the full Canada Post letter set, which additionally
/// excludes D, F, I, O, Q, U from the first position and D, F, I, O, Q, U from the rest.
/// It is more correct about what Canada Post actually issues, but it is stricter than the
/// frozen contract, and this slice should not reject a postal code the contract says is
/// well-formed. Easy to tighten later; impossible to loosen once clients depend on it.
/// </para>
/// </remarks>
public readonly record struct PostalCode
{
    /// <summary>Length of the canonical form, including the space.</summary>
    public const int CanonicalLength = 7;

    private PostalCode(string value) => Value = value;

    /// <summary>The normalized postal code, e.g. <c>"K1A 0B1"</c>.</summary>
    public string Value { get; }

    /// <summary>
    /// Attempts to normalize and validate a postal code. Input may carry any amount of
    /// surrounding or internal whitespace and any casing.
    /// </summary>
    public static bool TryParse(string? value, out PostalCode postalCode)
    {
        postalCode = default;

        if (value is null)
        {
            return false;
        }

        Span<char> compact = stackalloc char[6];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (length == 6)
            {
                return false;
            }

            compact[length++] = char.ToUpperInvariant(c);
        }

        if (length != 6)
        {
            return false;
        }

        // A1A1A1: letter, digit, letter, digit, letter, digit.
        for (var i = 0; i < 6; i++)
        {
            var expectLetter = i % 2 == 0;
            var c = compact[i];

            if (expectLetter ? !char.IsAsciiLetterUpper(c) : !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        postalCode = new PostalCode($"{compact[..3]} {compact[3..]}");
        return true;
    }

    /// <summary>Normalizes and validates a postal code.</summary>
    /// <exception cref="ArgumentException">The value is not a well-formed Canadian postal code.</exception>
    public static PostalCode Parse(string? value)
    {
        if (!TryParse(value, out var postalCode))
        {
            throw new ArgumentException($"'{value}' is not a Canadian postal code.", nameof(value));
        }

        return postalCode;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

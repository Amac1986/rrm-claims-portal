namespace ClaimPortal.Domain.Claims;

/// <summary>
/// The person filing the claim. Values are trimmed; the contract's length rules are
/// enforced by the request validator, not here — see <see cref="Claim"/> for why.
/// </summary>
public sealed record Claimant
{
    /// <summary>Creates a claimant, trimming each field.</summary>
    /// <exception cref="ArgumentException">Any field is null, empty or whitespace.</exception>
    public Claimant(string firstName, string lastName, string email, string phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        Email = email.Trim();
        Phone = phone.Trim();
    }

    /// <summary>Given name.</summary>
    public string FirstName { get; }

    /// <summary>Family name.</summary>
    public string LastName { get; }

    /// <summary>Address the confirmation email goes to.</summary>
    public string Email { get; }

    /// <summary>Contact telephone number, as the customer typed it.</summary>
    public string Phone { get; }
}

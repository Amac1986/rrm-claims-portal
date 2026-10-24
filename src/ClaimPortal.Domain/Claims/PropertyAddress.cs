namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Canadian address of the insured property. Province and postal code are stored as
/// parsed value objects, so an address that exists is an address whose province is one of
/// the thirteen codes and whose postal code is already in canonical <c>A1A 1A1</c> form.
/// </summary>
public sealed record PropertyAddress
{
    /// <summary>Creates an address, trimming the free-text fields.</summary>
    /// <exception cref="ArgumentException"><paramref name="addressLine1"/> or <paramref name="city"/> is blank.</exception>
    public PropertyAddress(
        string addressLine1,
        string? addressLine2,
        string city,
        Province province,
        PostalCode postalCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressLine1);
        ArgumentException.ThrowIfNullOrWhiteSpace(city);

        AddressLine1 = addressLine1.Trim();
        AddressLine2 = string.IsNullOrWhiteSpace(addressLine2) ? null : addressLine2.Trim();
        City = city.Trim();
        Province = province;
        PostalCode = postalCode;
    }

    /// <summary>Street address.</summary>
    public string AddressLine1 { get; }

    /// <summary>Unit, suite or similar. Blank input is stored as <see langword="null"/>.</summary>
    public string? AddressLine2 { get; }

    /// <summary>City, town or municipality.</summary>
    public string City { get; }

    /// <summary>Province or territory.</summary>
    public Province Province { get; }

    /// <summary>Postal code in canonical <c>A1A 1A1</c> form.</summary>
    public PostalCode PostalCode { get; }
}

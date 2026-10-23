namespace ClaimPortal.Contracts.Api;

/// <summary>
/// Request body for <c>POST /api/claims</c>. Serialized camelCase on the wire.
/// Validation rules live in ClaimPortal.Api; this type only defines the shape.
/// This contract is frozen — see <c>docs/CONTRACT.md</c>.
/// </summary>
public sealed record SubmitClaimRequest
{
    public string? PolicyNumber { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    /// <summary>Date of the incident, <c>YYYY-MM-DD</c>.</summary>
    public DateOnly? IncidentDate { get; init; }

    public LossType? LossType { get; init; }

    public string? Description { get; init; }

    /// <summary>Optional customer estimate of the loss, in CAD.</summary>
    public decimal? EstimatedLossAmount { get; init; }

    public PropertyAddress? PropertyAddress { get; init; }
}

/// <summary>Canadian address of the insured property.</summary>
public sealed record PropertyAddress
{
    public string? AddressLine1 { get; init; }

    public string? AddressLine2 { get; init; }

    public string? City { get; init; }

    /// <summary>Two-letter Canadian province or territory code, uppercase.</summary>
    public string? Province { get; init; }

    /// <summary>Canadian postal code, normalized to <c>A1A 1A1</c>.</summary>
    public string? PostalCode { get; init; }
}

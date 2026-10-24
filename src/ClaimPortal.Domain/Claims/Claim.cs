namespace ClaimPortal.Domain.Claims;

/// <summary>
/// A submitted first-notice-of-loss claim.
/// </summary>
/// <remarks>
/// <para>
/// The entity guards the invariants that must hold no matter how a claim arrives —
/// nothing blank, a parsed province and postal code, a real claim number, a UTC
/// timestamp. It deliberately does <em>not</em> re-check the contract's length rules
/// (policy number ≤ 32, description 20–2000, and so on). Those are properties of the
/// HTTP request: they exist so the React form can show a message under a specific field,
/// and they belong in the FluentValidation validator where that mapping lives. Encoding
/// them twice would mean two definitions of "valid" drifting apart, and the domain copy
/// would throw exceptions nobody can turn back into per-field errors.
/// </para>
/// <para>
/// There are no setters: the only mutation a claim allows in this slice is
/// <see cref="ReissueClaimNumber"/>, and only because the database, not the generator, is
/// the authority on claim-number uniqueness.
/// </para>
/// </remarks>
public sealed class Claim
{
    // Parameterless constructor for EF Core materialization. EF binds the backing
    // properties directly; nothing else should use this.
    private Claim()
    {
        PolicyNumber = null!;
        Claimant = null!;
        Description = null!;
        PropertyAddress = null!;
    }

    private Claim(
        Guid id,
        ClaimNumber claimNumber,
        string policyNumber,
        Claimant claimant,
        DateOnly incidentDate,
        LossType lossType,
        string description,
        decimal? estimatedLossAmount,
        PropertyAddress propertyAddress,
        DateTimeOffset submittedAt)
    {
        Id = id;
        ClaimNumber = claimNumber;
        PolicyNumber = policyNumber;
        Claimant = claimant;
        IncidentDate = incidentDate;
        LossType = lossType;
        Description = description;
        EstimatedLossAmount = estimatedLossAmount;
        PropertyAddress = propertyAddress;
        SubmittedAt = submittedAt;
        Status = ClaimStatus.Received;
    }

    /// <summary>
    /// Primary key. A UUIDv7, so the values are time-ordered: inserts land at the right
    /// edge of the index instead of scattering across it the way UUIDv4 does, which keeps
    /// the B-tree compact under a write-heavy table. The claim number is <em>not</em> the
    /// key — it is customer-facing, and a key the customer can read is a key somebody
    /// will eventually want to change.
    /// </summary>
    public Guid Id { get; private init; }

    /// <summary>Customer-facing claim number. Unique, enforced by a database index.</summary>
    public ClaimNumber ClaimNumber { get; private set; }

    /// <summary>Policy the claim is filed against.</summary>
    public string PolicyNumber { get; private init; }

    /// <summary>Person filing the claim.</summary>
    public Claimant Claimant { get; private init; }

    /// <summary>Calendar date of the incident. A date, not a timestamp.</summary>
    public DateOnly IncidentDate { get; private init; }

    /// <summary>Category of loss.</summary>
    public LossType LossType { get; private init; }

    /// <summary>The customer's account of what happened.</summary>
    public string Description { get; private init; }

    /// <summary>Optional customer estimate of the loss, in CAD.</summary>
    public decimal? EstimatedLossAmount { get; private init; }

    /// <summary>Address of the insured property.</summary>
    public PropertyAddress PropertyAddress { get; private init; }

    /// <summary>When the API accepted the claim. Always UTC.</summary>
    public DateTimeOffset SubmittedAt { get; private init; }

    /// <summary>Lifecycle state. Always <see cref="ClaimStatus.Received"/> in this slice.</summary>
    public ClaimStatus Status { get; private init; }

    /// <summary>
    /// Creates a claim in the <see cref="ClaimStatus.Received"/> state.
    /// </summary>
    /// <param name="claimNumber">Candidate claim number from <see cref="IClaimNumberGenerator"/>.</param>
    /// <param name="policyNumber">Policy the claim is filed against.</param>
    /// <param name="claimant">Person filing the claim.</param>
    /// <param name="incidentDate">Calendar date of the incident.</param>
    /// <param name="lossType">Category of loss.</param>
    /// <param name="description">The customer's account of what happened.</param>
    /// <param name="estimatedLossAmount">Optional estimate, in CAD.</param>
    /// <param name="propertyAddress">Address of the insured property.</param>
    /// <param name="submittedAt">When the API accepted the claim; converted to UTC.</param>
    /// <exception cref="ArgumentException">A required string is blank, or the estimate is negative.</exception>
    public static Claim Create(
        ClaimNumber claimNumber,
        string policyNumber,
        Claimant claimant,
        DateOnly incidentDate,
        LossType lossType,
        string description,
        decimal? estimatedLossAmount,
        PropertyAddress propertyAddress,
        DateTimeOffset submittedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyNumber);
        ArgumentNullException.ThrowIfNull(claimant);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(propertyAddress);

        if (estimatedLossAmount is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimatedLossAmount),
                estimatedLossAmount,
                "Estimated loss amount cannot be negative.");
        }

        if (claimNumber.Value is null)
        {
            throw new ArgumentException("A claim number is required.", nameof(claimNumber));
        }

        return new Claim(
            // UUIDv7 rather than Guid.NewGuid(): see Id.
            Guid.CreateVersion7(submittedAt),
            claimNumber,
            policyNumber.Trim(),
            claimant,
            incidentDate,
            lossType,
            description.Trim(),
            estimatedLossAmount,
            propertyAddress,
            submittedAt.ToUniversalTime());
    }

    /// <summary>
    /// Replaces the claim number after the database rejected the previous one as a
    /// duplicate. Only the persistence retry should call this.
    /// </summary>
    /// <remarks>
    /// The alternative — reconstructing the whole claim — would change <see cref="Id"/>
    /// on every retry, and the id is already in flight: it is the route the 201's
    /// <c>Location</c> header points at and the key the outbox message carries.
    /// </remarks>
    public void ReissueClaimNumber(ClaimNumber claimNumber)
    {
        if (claimNumber.Value is null)
        {
            throw new ArgumentException("A claim number is required.", nameof(claimNumber));
        }

        ClaimNumber = claimNumber;
    }
}

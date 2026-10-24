namespace ClaimPortal.Notifications.Templating;

/// <summary>
/// The view model the Scriban templates bind to.
/// </summary>
/// <remarks>
/// Every member is an already-formatted string. All date, time zone and label decisions are
/// made in C# where they are unit-testable, and the templates stay pure layout — no
/// <c>date.to_string</c> calls, no enum-to-label branching in markup.
/// </remarks>
public sealed record ClaimConfirmationModel
{
    public required string ClaimNumber { get; init; }

    public required string PolicyNumber { get; init; }

    public required string FirstName { get; init; }

    public required string FullName { get; init; }

    /// <summary>Customer-facing loss category, e.g. <c>Water damage</c>.</summary>
    public required string LossTypeLabel { get; init; }

    /// <summary>Incident date, e.g. <c>October 24, 2026</c>.</summary>
    public required string IncidentDate { get; init; }

    /// <summary>Submission timestamp in the configured display zone, e.g. <c>October 26, 2026 at 9:03 AM CDT</c>.</summary>
    public required string SubmittedAt { get; init; }

    public required string InsurerDisplayName { get; init; }

    public required string SupportPhone { get; init; }

    public required string SupportEmail { get; init; }
}

using System.ComponentModel.DataAnnotations;

namespace ClaimPortal.Notifications.Templating;

/// <summary>
/// The branding and contact details the confirmation email is written around. Configuration
/// rather than constants so a staging deployment can point customers at a staging phone
/// number, and so none of this is baked into the image.
/// </summary>
public sealed class NotificationContentOptions
{
    public const string SectionName = "NotificationContent";

    [Required(AllowEmptyStrings = false)]
    public string InsurerDisplayName { get; set; } = "Red River Mutual";

    [Required(AllowEmptyStrings = false)]
    public string SupportPhone { get; set; } = "1-800-370-2888";

    [Required(AllowEmptyStrings = false)]
    [EmailAddress]
    public string SupportEmail { get; set; } = "claims@redrivermutual.example";

    /// <summary>
    /// IANA time zone the email renders timestamps in. The event carries UTC per contract §6;
    /// a customer in Manitoba should not have to convert it. Falls back to UTC (with a warning)
    /// if the host has no tz database entry, so a missing <c>tzdata</c> package in a slim
    /// container degrades the wording rather than failing the send.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string DisplayTimeZone { get; set; } = "America/Winnipeg";
}

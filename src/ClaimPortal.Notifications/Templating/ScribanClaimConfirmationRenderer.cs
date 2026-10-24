using System.Globalization;
using ClaimPortal.Contracts;
using ClaimPortal.Notifications.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scriban;
using Scriban.Runtime;

namespace ClaimPortal.Notifications.Templating;

/// <summary>
/// Renders the claim confirmation from Scriban templates embedded in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// Templates are parsed once in the constructor. Registered as a singleton, that means a
/// template syntax error takes the worker down at startup with the Scriban error list
/// attached, rather than surfacing as a dead-lettered message the first time a claim is
/// filed. Parsing is also ~100x the cost of rendering, so this is the hot path too.
/// </para>
/// <para>
/// Rejected alternative: Razor (<c>RazorLight</c>). Razor would give compile-time checking of
/// the model, but it drags the Razor compiler and a runtime-compilation story into a worker
/// image for three templates, and templates authored by non-developers are the normal reason
/// to want a mail template engine. Scriban is a single BSD-2-Clause assembly with no
/// compilation step.
/// </para>
/// </remarks>
public sealed class ScribanClaimConfirmationRenderer : IClaimConfirmationRenderer
{
    private const string TemplateResourcePrefix =
        "ClaimPortal.Notifications.Templating.Templates.ClaimConfirmation.";

    /// <summary>
    /// Templates address the model with its C# property names (<c>{{ ClaimNumber }}</c>)
    /// rather than Scriban's default snake_case. The names then match
    /// <see cref="ClaimConfirmationModel"/> and docs/CONTRACT.md §2 exactly, so a reviewer
    /// can diff template against contract without a mental renaming step.
    /// </summary>
    private static readonly MemberRenamerDelegate PascalCaseMembers = member => member.Name;

    private readonly Template _subjectTemplate;
    private readonly Template _htmlTemplate;
    private readonly Template _textTemplate;
    private readonly IOptionsMonitor<NotificationContentOptions> _content;
    private readonly TimeZoneInfo _displayTimeZone;

    public ScribanClaimConfirmationRenderer(
        IOptionsMonitor<NotificationContentOptions> content,
        ILogger<ScribanClaimConfirmationRenderer> logger)
    {
        _content = content;

        _subjectTemplate = ParseEmbedded("Subject.sbn");
        _htmlTemplate = ParseEmbedded("Html.sbn");
        _textTemplate = ParseEmbedded("Text.sbn");

        _displayTimeZone = ResolveTimeZone(content.CurrentValue.DisplayTimeZone, logger);
    }

    public EmailMessage Render(ClaimSubmitted claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var content = _content.CurrentValue;
        var model = BuildModel(claim, content);

        return new EmailMessage
        {
            ToAddress = claim.ClaimantEmail,
            ToDisplayName = model.FullName,
            // A header value containing CR or LF is a header-injection vector; MimeKit would
            // reject it, but trimming the newline the subject template's trailing EOL adds is
            // needed regardless, so normalise all whitespace to single spaces here.
            Subject = CollapseWhitespace(_subjectTemplate.Render(model, PascalCaseMembers)),

            // Trimmed because the templates open with an authoring comment and close with a
            // trailing newline; neither is content. A leading blank line before <!DOCTYPE> in
            // particular makes some clients treat the part as text rather than markup.
            HtmlBody = _htmlTemplate.Render(model, PascalCaseMembers).Trim(),
            TextBody = _textTemplate.Render(model, PascalCaseMembers).Trim(),
        };
    }

    internal ClaimConfirmationModel BuildModel(ClaimSubmitted claim, NotificationContentOptions content)
    {
        var fullName = $"{claim.ClaimantFirstName} {claim.ClaimantLastName}".Trim();
        var submittedLocal = TimeZoneInfo.ConvertTime(claim.SubmittedAt, _displayTimeZone);

        return new ClaimConfirmationModel
        {
            ClaimNumber = claim.ClaimNumber,
            PolicyNumber = claim.PolicyNumber,
            FirstName = claim.ClaimantFirstName,
            FullName = fullName,
            LossTypeLabel = DescribeLossType(claim.LossType),

            // en-CA so the customer sees "October 24, 2026", not the host locale's format.
            // Container locale is not something this code should depend on.
            IncidentDate = claim.IncidentDate.ToString("MMMM d, yyyy", Formatting),
            SubmittedAt = string.Create(
                Formatting,
                $"{submittedLocal:MMMM d, yyyy} at {submittedLocal:h:mm} {Meridiem(submittedLocal)} {Abbreviate(submittedLocal)}"),

            InsurerDisplayName = content.InsurerDisplayName,
            SupportPhone = content.SupportPhone,
            SupportEmail = content.SupportEmail,
        };
    }

    private static readonly CultureInfo Formatting = CultureInfo.GetCultureInfo("en-CA");

    /// <summary>
    /// Customer-facing wording for the loss category. The contract serialises the raw
    /// PascalCase enum name (§3); "Water" alone reads oddly in a sentence about damage.
    /// Mapped here rather than with a <c>[Display]</c> attribute because
    /// <c>ClaimPortal.Contracts</c> is frozen and customer-facing copy is this service's
    /// concern, not the contract's. The default arm throws instead of falling back to the
    /// enum name: a new loss type must be worded deliberately, and a dead-lettered message
    /// naming the unmapped value is a louder signal than an email reading "Earthquake".
    /// <c>LossTypeCoversEveryContractValue</c> in the test suite is what actually catches it.
    /// </summary>
    private static string DescribeLossType(LossType lossType) => lossType switch
    {
        LossType.Fire => "Fire damage",
        LossType.Water => "Water damage",
        LossType.Wind => "Wind damage",
        LossType.Hail => "Hail damage",
        LossType.Theft => "Theft",
        LossType.Vandalism => "Vandalism",
        LossType.Other => "Other",
        _ => throw new ArgumentOutOfRangeException(
            nameof(lossType), lossType, "No customer-facing wording is defined for this loss type."),
    };

    private string Abbreviate(DateTimeOffset localTime)
    {
        // Special-cased because TimeZoneInfo.Utc.StandardName is "Coordinated Universal Time",
        // which the initials rule below would render as "CUT". This is the arm the tzdata
        // fallback lands on, so it is the one a reviewer is most likely to see.
        if (_displayTimeZone.Equals(TimeZoneInfo.Utc))
        {
            return "UTC";
        }

        return _displayTimeZone.IsDaylightSavingTime(localTime)
            ? ShortZoneName(_displayTimeZone.DaylightName)
            : ShortZoneName(_displayTimeZone.StandardName);
    }

    /// <summary>
    /// "Central Daylight Time" to "CDT". Windows and Linux hand back different casing and
    /// wording for the same zone, so take initials rather than hard-coding a lookup table.
    /// </summary>
    private static string ShortZoneName(string zoneName)
    {
        var initials = zoneName
            .Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => char.IsLetter(word[0]))
            .Select(word => char.ToUpperInvariant(word[0]));

        var abbreviation = string.Concat(initials);
        return abbreviation.Length is >= 2 and <= 5 ? abbreviation : zoneName;
    }

    private static TimeZoneInfo ResolveTimeZone(string id, ILogger logger)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A slim container image without tzdata should still send correct emails.
            logger.LogWarning(
                ex,
                "Time zone {DisplayTimeZone} is not available on this host; confirmation emails will show UTC timestamps.",
                id);
            return TimeZoneInfo.Utc;
        }
    }

    private static Template ParseEmbedded(string suffix)
    {
        var resourceName = TemplateResourcePrefix + suffix;
        var assembly = typeof(ScribanClaimConfirmationRenderer).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Email template '{resourceName}' is missing from {assembly.GetName().Name}. "
                + $"Known resources: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        var template = Template.Parse(reader.ReadToEnd(), resourceName);

        if (template.HasErrors)
        {
            throw new InvalidOperationException(
                $"Email template '{resourceName}' failed to parse: {string.Join("; ", template.Messages)}");
        }

        return template;
    }

    /// <summary>
    /// Canadian-style meridiem, written out rather than taken from the culture's
    /// <c>AMDesignator</c>.
    /// </summary>
    /// <remarks>
    /// The <c>tt</c> format specifier would render whatever the active ICU/CLDR data says, and
    /// that has moved: recent CLDR versions separate the designator with a narrow no-break
    /// space (U+202F), which survives neither quoted-printable encoding nor a support agent
    /// copying the timestamp into a claims system. Pinning the two strings here means the
    /// wording of a customer-facing email cannot change because a base image shipped new ICU
    /// data, and keeps the month names — which genuinely are a localisation concern — on en-CA.
    /// </remarks>
    private static string Meridiem(DateTimeOffset localTime) => localTime.Hour < 12 ? "a.m." : "p.m.";

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

namespace ClaimPortal.Notifications.Email;

/// <summary>
/// A fully rendered, transport-agnostic email. Produced by the templating layer and handed
/// to an <see cref="IEmailSender"/>; it carries no SMTP, SendGrid or ACS concepts so the
/// renderer can be unit tested with no mail infrastructure at all.
/// </summary>
public sealed record EmailMessage
{
    public required string ToAddress { get; init; }

    public required string ToDisplayName { get; init; }

    public required string Subject { get; init; }

    /// <summary>The <c>text/html</c> alternative.</summary>
    public required string HtmlBody { get; init; }

    /// <summary>
    /// The <c>text/plain</c> alternative. Always populated — the confirmation is sent as
    /// multipart/alternative so clients that refuse HTML still show a usable claim number.
    /// </summary>
    public required string TextBody { get; init; }
}

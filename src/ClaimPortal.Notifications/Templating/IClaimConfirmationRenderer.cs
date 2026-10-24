using ClaimPortal.Contracts;
using ClaimPortal.Notifications.Email;

namespace ClaimPortal.Notifications.Templating;

/// <summary>
/// Turns a <see cref="ClaimSubmitted"/> event into a ready-to-send
/// <see cref="EmailMessage"/>. Pure function of the event plus configuration: no I/O, no
/// SMTP, no database. That is what makes the email content testable on its own.
/// </summary>
public interface IClaimConfirmationRenderer
{
    EmailMessage Render(ClaimSubmitted claim);
}

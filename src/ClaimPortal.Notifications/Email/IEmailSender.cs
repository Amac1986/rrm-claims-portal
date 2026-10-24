namespace ClaimPortal.Notifications.Email;

/// <summary>
/// The seam between "what to say" and "how it leaves the building".
/// </summary>
/// <remarks>
/// SMTP is an implementation detail behind this interface. Moving production to SendGrid or
/// Azure Communication Services is a single DI registration swap in
/// <c>Program.cs</c> — no consumer, template or test changes. Implementations are expected
/// to translate provider failures into <see cref="TransientEmailDeliveryException"/> or
/// <see cref="PermanentEmailDeliveryException"/> so the retry policy can tell a mail server
/// that is briefly down from a message that will never be accepted.
/// </remarks>
public interface IEmailSender
{
    /// <summary>
    /// Delivers <paramref name="message"/>. Returning normally means the provider accepted
    /// responsibility for the message.
    /// </summary>
    /// <exception cref="TransientEmailDeliveryException">
    /// The failure is worth retrying (connection refused, timeout, SMTP 4xx, throttling).
    /// </exception>
    /// <exception cref="PermanentEmailDeliveryException">
    /// The failure will recur for every attempt (SMTP 5xx, malformed recipient).
    /// </exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

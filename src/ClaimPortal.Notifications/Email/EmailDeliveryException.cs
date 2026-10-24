namespace ClaimPortal.Notifications.Email;

/// <summary>Base type for failures raised by an <see cref="IEmailSender"/>.</summary>
public abstract class EmailDeliveryException : Exception
{
    protected EmailDeliveryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The mail provider could not accept the message right now, but an identical attempt later
/// may succeed. The retry and delayed-redelivery policies are scoped to this type.
/// </summary>
public sealed class TransientEmailDeliveryException : EmailDeliveryException
{
    public TransientEmailDeliveryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The mail provider rejected the message in a way that will repeat on every attempt.
/// Deliberately excluded from the retry policy: burning the retry budget on a permanent
/// rejection only delays the message reaching <c>_error</c>, where a human can see it.
/// </summary>
public sealed class PermanentEmailDeliveryException : EmailDeliveryException
{
    public PermanentEmailDeliveryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

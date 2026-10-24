using ClaimPortal.Notifications.Email;

namespace ClaimPortal.Notifications.Tests.Support;

/// <summary>
/// An <see cref="IEmailSender"/> that counts attempts, keeps what it accepted, and can be
/// scripted to fail on chosen attempts.
/// </summary>
/// <remarks>
/// The existence of this class is the point of the <see cref="IEmailSender"/> seam: every
/// behaviour the assignment asks about — retried, faulted, deduplicated — is provable without
/// an SMTP server, and the production path differs only in which implementation is registered.
/// <see cref="Attempts"/> versus <see cref="Accepted"/> is the distinction the tests turn on:
/// attempts counts calls, accepted counts emails that actually left.
/// </remarks>
internal sealed class RecordingEmailSender : IEmailSender
{
    private readonly Lock _gate = new();
    private readonly List<EmailMessage> _accepted = [];
    private int _attempts;

    /// <summary>
    /// Given the 1-based attempt number, returns the exception to throw, or <c>null</c> to
    /// accept the message. Default: accept everything.
    /// </summary>
    internal Func<int, Exception?> Behaviour { get; init; } = _ => null;

    /// <summary>Every call to <see cref="SendAsync"/>, including the ones that threw.</summary>
    internal int Attempts
    {
        get
        {
            lock (_gate)
            {
                return _attempts;
            }
        }
    }

    /// <summary>Only the messages this sender accepted — i.e. emails the customer received.</summary>
    internal IReadOnlyList<EmailMessage> Accepted
    {
        get
        {
            lock (_gate)
            {
                return [.. _accepted];
            }
        }
    }

    internal EmailMessage SingleAccepted
    {
        get
        {
            var accepted = Accepted;
            if (accepted.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one accepted email but the sender accepted {accepted.Count}.");
            }

            return accepted[0];
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        int attempt;
        lock (_gate)
        {
            attempt = ++_attempts;
        }

        if (Behaviour(attempt) is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _accepted.Add(message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Fails the first <paramref name="count"/> attempts transiently, then accepts.</summary>
    internal static Func<int, Exception?> FailTransientlyTimes(int count) =>
        attempt => attempt <= count
            ? new TransientEmailDeliveryException($"Simulated transient SMTP failure on attempt {attempt}.")
            : null;

    /// <summary>Always fails in a way the retry policy must not retry.</summary>
    internal static Func<int, Exception?> AlwaysFailPermanently() =>
        attempt => new PermanentEmailDeliveryException(
            $"Simulated SMTP 550 rejection on attempt {attempt}.");

    /// <summary>Always fails in a way the retry policy must retry.</summary>
    internal static Func<int, Exception?> AlwaysFailTransiently() =>
        attempt => new TransientEmailDeliveryException(
            $"Simulated unreachable relay on attempt {attempt}.");
}

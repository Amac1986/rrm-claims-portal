namespace ClaimPortal.Notifications.Idempotency;

/// <summary>Lifecycle of a single confirmation email.</summary>
public enum NotificationDeliveryStatus
{
    /// <summary>
    /// An attempt owns this message id and has not reported an outcome. Either it is running
    /// right now, or the process died mid-attempt.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The mail provider accepted the message. This is the terminal state and the one that
    /// makes a redelivery a no-op.
    /// </summary>
    Sent = 1,

    /// <summary>
    /// An attempt failed and reported it. We know the provider did <em>not</em> accept the
    /// message, so a redelivery may retry immediately without risking a duplicate.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// One row per <c>ClaimSubmitted</c> message id. The unique constraint on the primary key is
/// the actual idempotency mechanism; everything else on this row exists so an operator can
/// answer "did this customer get their email, and if not, why not?" from SQL.
/// </summary>
public sealed class NotificationDelivery
{
    /// <summary>
    /// The transport <c>MessageId</c> — per contract §2 the outbox row id, stable across
    /// redeliveries. The primary key, so the uniqueness guarantee is the table's, not ours.
    /// </summary>
    public Guid MessageId { get; set; }

    /// <summary>Denormalised from the event purely so support can find a row by claim.</summary>
    public Guid ClaimId { get; set; }

    public string ClaimNumber { get; set; } = string.Empty;

    public string Recipient { get; set; } = string.Empty;

    public NotificationDeliveryStatus Status { get; set; }

    /// <summary>Number of attempts that have owned this row, including the current one.</summary>
    public int AttemptCount { get; set; }

    public DateTimeOffset FirstAttemptedAt { get; set; }

    public DateTimeOffset LastAttemptedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>
    /// Last failure message, truncated. Diagnostics only — never used to make a decision,
    /// and deliberately not the exception's stack trace, which does not belong in a table
    /// that support staff read.
    /// </summary>
    public string? LastError { get; set; }
}

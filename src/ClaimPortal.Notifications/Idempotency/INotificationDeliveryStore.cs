namespace ClaimPortal.Notifications.Idempotency;

/// <summary>Identifying details recorded the first time a message id is seen.</summary>
/// <param name="MessageId">Transport <c>MessageId</c>; the idempotency key (contract §2).</param>
public readonly record struct DeliveryAttemptKey(
    Guid MessageId,
    Guid ClaimId,
    string ClaimNumber,
    string Recipient);

/// <summary>What happened when a consumer asked to own a message id.</summary>
public enum DeliveryClaimOutcome
{
    /// <summary>This attempt owns the message id and must perform the send.</summary>
    Claimed,

    /// <summary>
    /// The email has already been accepted by the mail provider. The caller must not send.
    /// This is the branch that satisfies "redelivery sends exactly one email".
    /// </summary>
    AlreadySent,

    /// <summary>
    /// Another attempt owns the message id and has not reported an outcome yet. The caller
    /// must not send. See <see cref="EfNotificationDeliveryStore"/> for why this is the safe
    /// answer and what the stale-claim window is for.
    /// </summary>
    AttemptInFlight,
}

/// <param name="Outcome">Whether the caller may send.</param>
/// <param name="AttemptCount">Attempts recorded against this message id, including this one.</param>
/// <param name="SentAt">When the provider accepted the email, if it has.</param>
public readonly record struct DeliveryClaim(
    DeliveryClaimOutcome Outcome,
    int AttemptCount,
    DateTimeOffset? SentAt)
{
    public bool MaySend => Outcome == DeliveryClaimOutcome.Claimed;
}

/// <summary>
/// Durable record of which confirmation emails have been sent, keyed by message id.
/// </summary>
/// <remarks>
/// The three operations are deliberately separate rather than one "run this under a lock"
/// callback: the claim and the outcome must be committed as independent, short transactions
/// so no database transaction is held open across an SMTP round trip.
/// </remarks>
public interface INotificationDeliveryStore
{
    /// <summary>
    /// Atomically attempts to take ownership of <paramref name="key"/>. Must be a single
    /// compare-and-set against the uniqueness constraint — a read followed by a write leaves
    /// a window in which two consumers both decide to send.
    /// </summary>
    Task<DeliveryClaim> TryClaimAsync(DeliveryAttemptKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the mail provider accepted the email. Called after the provider returns
    /// and before the message is acked, which is what makes a crash in that window safe.
    /// </summary>
    Task MarkSentAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that this attempt failed, releasing the claim so a retry can take it
    /// immediately. Never overwrites <see cref="NotificationDeliveryStatus.Sent"/>.
    /// </summary>
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken cancellationToken);
}

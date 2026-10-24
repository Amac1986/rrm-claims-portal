using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaimPortal.Notifications.Idempotency;

/// <summary>
/// The exactly-one-email guarantee, implemented as a claim/confirm protocol over
/// <c>notification_deliveries</c>.
/// </summary>
/// <remarks>
/// <para><b>The protocol.</b> For each message id:</para>
/// <list type="number">
///   <item><description>
///     <c>INSERT</c> a <c>Pending</c> row. The primary key is the message id, so exactly one
///     concurrent attempt can win; the losers get a unique-violation
///     <see cref="DbUpdateException"/>. This is the atomic compare-and-set — there is no
///     <c>SELECT</c>-then-<c>INSERT</c> window anywhere in this class.
///   </description></item>
///   <item><description>The winner sends the email.</description></item>
///   <item><description>
///     The winner sets the row to <c>Sent</c> <em>after</em> the provider accepted it and
///     <em>before</em> the message is acked. A crash in that window redelivers the message,
///     which then reads <c>Sent</c> and sends nothing. That is the scenario the contract calls
///     out and <c>RedeliveryAfterSuccessfulSendDoesNotSendASecondEmail</c> proves.
///   </description></item>
/// </list>
/// <para>
/// <b>Why not write the row before sending and commit once?</b> Because SMTP is not in the
/// transaction. Marking <c>Sent</c> first means an SMTP failure leaves a row claiming the
/// customer was emailed when they were not, and the confirmation is lost permanently with no
/// trace. Marking it after means a crash in a one-write window can send a second email. A
/// duplicate confirmation is an annoyance; a silently missing one is the failure that matters
/// for an insurance claim, so the ordering is chosen to never lose an email.
/// </para>
/// <para>
/// <b>Residual risk, stated plainly:</b> a process killed between "SMTP accepted" and the
/// <see cref="MarkSentAsync"/> commit will send the email twice. Closing that completely needs
/// the mail provider to deduplicate on an idempotency key we supply (SendGrid and ACS both
/// can), which is a change behind <c>IEmailSender</c> and is noted in the ADR as the next step
/// if the business ever asks for it.
/// </para>
/// </remarks>
public sealed class EfNotificationDeliveryStore : INotificationDeliveryStore
{
    private readonly NotificationsDbContext _db;
    private readonly IOptionsMonitor<IdempotencyOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<EfNotificationDeliveryStore> _logger;

    public EfNotificationDeliveryStore(
        NotificationsDbContext db,
        IOptionsMonitor<IdempotencyOptions> options,
        TimeProvider clock,
        ILogger<EfNotificationDeliveryStore> logger)
    {
        _db = db;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DeliveryClaim> TryClaimAsync(DeliveryAttemptKey key, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        for (var attempt = 1; attempt <= options.MaxClaimAttempts; attempt++)
        {
            if (await TryInsertClaimAsync(key, cancellationToken).ConfigureAwait(false))
            {
                return new DeliveryClaim(DeliveryClaimOutcome.Claimed, AttemptCount: 1, SentAt: null);
            }

            var existing = await ReadAsync(key.MessageId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                // The row was deleted between the failed insert and this read — only possible
                // if an operator pruned it. Go round again and try to insert it afresh.
                continue;
            }

            switch (existing.Status)
            {
                case NotificationDeliveryStatus.Sent:
                    return new DeliveryClaim(
                        DeliveryClaimOutcome.AlreadySent, existing.AttemptCount, existing.SentAt);

                case NotificationDeliveryStatus.Failed:
                    // A previous attempt reported failure, so we know the provider did not
                    // accept the email. Safe to take the claim and retry immediately.
                    if (await TryTakeOverAsync(existing, matchLastAttemptedAt: false, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return new DeliveryClaim(
                            DeliveryClaimOutcome.Claimed, existing.AttemptCount + 1, SentAt: null);
                    }

                    break;

                case NotificationDeliveryStatus.Pending:
                    var age = _clock.GetUtcNow() - existing.LastAttemptedAt;
                    if (age < options.StaleClaimAfter)
                    {
                        // Another attempt holds the claim and may still be mid-send. Do not
                        // send. If it succeeds, the customer is emailed; if its process died,
                        // the broker redelivers and the stale branch below recovers the row.
                        return new DeliveryClaim(
                            DeliveryClaimOutcome.AttemptInFlight, existing.AttemptCount, SentAt: null);
                    }

                    _logger.LogWarning(
                        "Delivery claim for message {MessageId} (claim {ClaimNumber}) has been Pending for "
                        + "{StaleForSeconds:F0}s, longer than the {StaleClaimAfter} window. Assuming the "
                        + "attempt that owned it died and taking the claim.",
                        existing.MessageId,
                        existing.ClaimNumber,
                        age.TotalSeconds,
                        options.StaleClaimAfter);

                    // matchLastAttemptedAt pins the update to the exact row version we read, so
                    // if two redeliveries both decide a claim is stale only one takes it.
                    if (await TryTakeOverAsync(existing, matchLastAttemptedAt: true, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return new DeliveryClaim(
                            DeliveryClaimOutcome.Claimed, existing.AttemptCount + 1, SentAt: null);
                    }

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown delivery status {existing.Status} on message {existing.MessageId}.");
            }
        }

        // Every round lost a race. Report in-flight rather than sending: the caller treats this
        // as "someone else owns it", which is always the safe answer.
        _logger.LogWarning(
            "Gave up claiming message {MessageId} after {MaxClaimAttempts} attempts; another consumer "
            + "keeps winning the race for claim {ClaimNumber}.",
            key.MessageId,
            options.MaxClaimAttempts,
            key.ClaimNumber);

        return new DeliveryClaim(DeliveryClaimOutcome.AttemptInFlight, AttemptCount: 0, SentAt: null);
    }

    public async Task MarkSentAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        var rows = await _db.NotificationDeliveries
            .Where(delivery => delivery.MessageId == messageId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Sent)
                    .SetProperty(delivery => delivery.SentAt, now)
                    .SetProperty(delivery => delivery.LastAttemptedAt, now)
                    .SetProperty(delivery => delivery.LastError, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows != 1)
        {
            // The email has gone out but we cannot record it. Throwing here would redeliver the
            // message and send a second copy, so log loudly and let the message ack.
            _logger.LogError(
                "Sent the confirmation email for message {MessageId} but could not mark it Sent: "
                + "{RowsAffected} rows matched. A redelivery of this message would send a duplicate.",
                messageId,
                rows);
        }
    }

    public async Task MarkFailedAsync(Guid messageId, string error, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var truncated = Truncate(error, NotificationDeliveryConfiguration.MaxErrorLength);

        // The Status guard is what stops a late failure report from overwriting a Sent row and
        // re-opening the claim — which would let a redelivery send a second email.
        var rows = await _db.NotificationDeliveries
            .Where(delivery => delivery.MessageId == messageId
                && delivery.Status != NotificationDeliveryStatus.Sent)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Failed)
                    .SetProperty(delivery => delivery.LastAttemptedAt, now)
                    .SetProperty(delivery => delivery.LastError, truncated),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows != 1)
        {
            _logger.LogWarning(
                "Could not record a delivery failure for message {MessageId}: {RowsAffected} rows matched. "
                + "The row is most likely already Sent.",
                messageId,
                rows);
        }
    }

    /// <summary>
    /// The compare-and-set. Returns <c>false</c> when the primary key already exists, which is
    /// the uniqueness constraint doing the deduplication.
    /// </summary>
    private async Task<bool> TryInsertClaimAsync(DeliveryAttemptKey key, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        var delivery = new NotificationDelivery
        {
            MessageId = key.MessageId,
            ClaimId = key.ClaimId,
            ClaimNumber = key.ClaimNumber,
            Recipient = key.Recipient,
            Status = NotificationDeliveryStatus.Pending,
            AttemptCount = 1,
            FirstAttemptedAt = now,
            LastAttemptedAt = now,
        };

        _db.NotificationDeliveries.Add(delivery);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Detach or the next SaveChanges on this scoped context retries the insert.
            _db.Entry(delivery).State = EntityState.Detached;

            // Only a primary-key collision means "another attempt got here first". Every other
            // write failure — a missing table, a dead connection, a full disk — must not be
            // reported as a duplicate: that would make TryClaimAsync answer "someone else owns
            // this" and the consumer would ack the message and silently never email the
            // customer. Confirmed by re-reading the row rather than by matching a provider
            // error code, which keeps this class provider-agnostic and lets the tests run the
            // real constraint on SQLite.
            if (await ExistsAsync(key.MessageId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            throw;
        }
    }

    private async Task<bool> TryTakeOverAsync(
        NotificationDelivery observed,
        bool matchLastAttemptedAt,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        var query = _db.NotificationDeliveries
            .Where(delivery => delivery.MessageId == observed.MessageId
                && delivery.Status == observed.Status);

        if (matchLastAttemptedAt)
        {
            query = query.Where(delivery => delivery.LastAttemptedAt == observed.LastAttemptedAt);
        }

        var rows = await query
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Pending)
                    .SetProperty(delivery => delivery.AttemptCount, delivery => delivery.AttemptCount + 1)
                    .SetProperty(delivery => delivery.LastAttemptedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>
    /// <c>AsNoTracking</c> throughout: every write in this class is a set-based
    /// <c>ExecuteUpdate</c>, so nothing should ever be resolved from the change tracker — a
    /// cached entity would hide exactly the concurrent modification we are looking for.
    /// </summary>
    private Task<NotificationDelivery?> ReadAsync(Guid messageId, CancellationToken cancellationToken) =>
        _db.NotificationDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(delivery => delivery.MessageId == messageId, cancellationToken);

    private Task<bool> ExistsAsync(Guid messageId, CancellationToken cancellationToken) =>
        _db.NotificationDeliveries
            .AsNoTracking()
            .AnyAsync(delivery => delivery.MessageId == messageId, cancellationToken);

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..(maxLength - 1)] + '…';
}

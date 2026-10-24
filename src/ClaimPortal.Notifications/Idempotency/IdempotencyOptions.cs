using System.ComponentModel.DataAnnotations;

namespace ClaimPortal.Notifications.Idempotency;

/// <summary>Tuning for the delivery-claim protocol in <see cref="EfNotificationDeliveryStore"/>.</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>
    /// How long a <c>Pending</c> row may sit untouched before a redelivery is allowed to take
    /// the claim away from the attempt that owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This window only exists to recover from a worker that was killed mid-attempt: that row
    /// would otherwise stay <c>Pending</c> forever and the customer would never get an email.
    /// </para>
    /// <para>
    /// It is the one setting here that can cause a duplicate email, so it is deliberately
    /// generous. An attempt can only be mid-send for <c>Smtp.TimeoutSeconds</c> (30s by
    /// default) plus the retry policy's own budget; five minutes is an order of magnitude more
    /// than that, so stealing a claim from a <em>live</em> attempt effectively cannot happen
    /// while a shorter window would make it a routine occurrence under load.
    /// </para>
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan StaleClaimAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bound on the claim/re-read loop. Each iteration is a lost race with another attempt for
    /// the same message id; more than a couple means something is badly wrong, and spinning
    /// forever against the database is worse than dead-lettering the message.
    /// </summary>
    [Range(1, 10)]
    public int MaxClaimAttempts { get; set; } = 3;
}

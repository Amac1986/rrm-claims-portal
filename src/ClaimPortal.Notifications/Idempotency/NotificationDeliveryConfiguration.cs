using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClaimPortal.Notifications.Idempotency;

/// <summary>
/// Maps <see cref="NotificationDelivery"/> onto <c>notification_deliveries</c>.
/// </summary>
/// <remarks>
/// The whole idempotency guarantee rests on one line in here: <c>MessageId</c> is the primary
/// key, so the uniqueness check is the database's job, enforced under concurrency, rather than
/// a <c>SELECT</c>-then-<c>INSERT</c> in application code that two consumers can interleave.
/// </remarks>
internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    /// <summary>
    /// Enough of the failure to be useful in a support query, capped so a pathological provider
    /// error message cannot bloat the row. Truncation happens in the store, not the database.
    /// </summary>
    internal const int MaxErrorLength = 1024;

    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");

        // The transport MessageId (contract §2: the outbox row id, stable across redeliveries).
        // ValueGeneratedNever: the id comes from the message, never from the database.
        builder.HasKey(delivery => delivery.MessageId);
        builder.Property(delivery => delivery.MessageId).ValueGeneratedNever();

        builder.Property(delivery => delivery.ClaimId).IsRequired();
        builder.Property(delivery => delivery.ClaimNumber).HasMaxLength(32).IsRequired();
        builder.Property(delivery => delivery.Recipient).HasMaxLength(254).IsRequired();

        // Stored as an int, not a string: the enum is internal to this service, and an int
        // keeps the partial index below small. The names are in NotificationDeliveryStatus.
        builder.Property(delivery => delivery.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(delivery => delivery.AttemptCount).IsRequired();
        builder.Property(delivery => delivery.FirstAttemptedAt).IsRequired();
        builder.Property(delivery => delivery.LastAttemptedAt).IsRequired();
        builder.Property(delivery => delivery.SentAt);
        builder.Property(delivery => delivery.LastError).HasMaxLength(MaxErrorLength);

        // Support path: "did this customer get their confirmation?" is asked by claim number,
        // which arrives from the customer, not by message id.
        builder.HasIndex(delivery => delivery.ClaimNumber);

        // Operations path: find deliveries stuck Pending (a worker that died mid-attempt) or
        // Failed, oldest first. Not declared as a Postgres partial index on purpose — that
        // would be provider-specific SQL in a model the tests run on SQLite, and the table is
        // one row per claim, so the saving would be invisible.
        builder.HasIndex(delivery => new { delivery.Status, delivery.LastAttemptedAt });
    }
}

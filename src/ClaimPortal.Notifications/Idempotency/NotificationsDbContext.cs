using Microsoft.EntityFrameworkCore;

namespace ClaimPortal.Notifications.Idempotency;

/// <summary>
/// The notification worker's own schema. One table: <c>notification_deliveries</c>.
/// </summary>
/// <remarks>
/// <para>
/// This context is deliberately separate from the API's <c>ClaimsDbContext</c> and lives in its
/// own <c>notifications</c> schema with its own migrations history table, even though both point
/// at the same Postgres instance locally. The worker must not be able to read or migrate the
/// claims tables: it is a different deployable with a different owner, and the only thing it
/// knows about a claim is what the event told it (contract §2, event-carried state transfer).
/// Sharing a context would quietly re-couple the two services and make "the consumer never
/// reads the claims table" a promise rather than a structural fact.
/// </para>
/// <para>
/// Rejected alternative: MassTransit's built-in <c>InMemoryOutbox</c>/<c>JobConsumer</c>
/// deduplication. Those dedupe inside a single bus instance or require the saga tables; a
/// plain table with a unique primary key is less machinery, survives a process restart, and is
/// something an operator can query during an incident.
/// </para>
/// </remarks>
public sealed class NotificationsDbContext : DbContext
{
    public const string Schema = "notifications";

    /// <summary>Separate history table so <c>dotnet ef</c> never fights the API's migrations.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
        : base(options)
    {
    }

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite — used by the idempotency tests because it enforces unique constraints — has
        // no concept of a schema. Applying it only where it means something keeps the test
        // model identical to production in every respect that the tests actually exercise.
        if (Database.IsNpgsql())
        {
            modelBuilder.HasDefaultSchema(Schema);
        }

        modelBuilder.ApplyConfiguration(new NotificationDeliveryConfiguration());
    }
}

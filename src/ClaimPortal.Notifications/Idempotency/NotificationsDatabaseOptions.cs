using System.ComponentModel.DataAnnotations;

namespace ClaimPortal.Notifications.Idempotency;

/// <summary>
/// Where the <c>notification_deliveries</c> table lives.
/// </summary>
/// <remarks>
/// The connection string is read from configuration under <c>Database:ConnectionString</c>,
/// which in a container means the <c>Database__ConnectionString</c> environment variable fed
/// from a secret. The default below is the throwaway local Postgres that the compose file
/// creates with the same throwaway password, and exists so <c>docker compose up</c> works from
/// cold with nothing configured. It is a local-only credential by construction — the database
/// it names is created empty on a developer's machine. No deployed credential is ever
/// committed; see docs/adr/0001-notification-worker.md.
/// </remarks>
public sealed class NotificationsDatabaseOptions
{
    public const string SectionName = "Database";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=claimportal;Username=claimportal;Password=claimportal";

    /// <summary>
    /// Provider-level retries for a dropped connection, distinct from the message retry
    /// policy: this one covers "Postgres was restarted", the other covers "the relay is down".
    /// </summary>
    [Range(0, 10)]
    public int MaxRetryCount { get; set; } = 5;

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether the worker applies its own migrations at startup.
    /// </summary>
    /// <remarks>
    /// True by default because the deliverable is "clone the repo and run <c>docker compose
    /// up</c>" — a reviewer should not have to run <c>dotnet ef database update</c>. In a real
    /// deployment this is false and migrations are a pipeline step, because two worker replicas
    /// racing to migrate at startup is a known way to deadlock a rollout.
    /// </remarks>
    public bool MigrateOnStartup { get; set; } = true;
}

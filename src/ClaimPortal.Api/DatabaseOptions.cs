using System.ComponentModel.DataAnnotations;

namespace ClaimPortal.Api;

/// <summary>Startup persistence behaviour.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// True by default so `docker compose up` on a clean volume needs no manual migration step.
    /// In a real deployment migrations are a pipeline step and this is false.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;

    [Range(0, 10)]
    public int MaxRetryCount { get; set; } = 5;

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
}

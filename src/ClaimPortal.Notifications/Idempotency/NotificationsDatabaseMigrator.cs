using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaimPortal.Notifications.Idempotency;

/// <summary>
/// Applies this worker's migrations at startup so that <c>docker compose up</c> on a clean
/// volume produces a working system with no manual step.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IHostedService"/> rather than code in <c>Program.cs</c> so the host's
/// lifetime and cancellation token apply: a reviewer who hits Ctrl+C while Postgres is still
/// starting gets a clean shutdown, not a hung process.
/// </para>
/// <para>
/// It runs before the bus starts consuming because hosted services start in registration
/// order and <c>AddMassTransit</c>'s bus service is registered after this one. The table is
/// therefore guaranteed to exist before the first <c>TryClaimAsync</c>.
/// </para>
/// <para>
/// Rejected alternative: <c>EnsureCreatedAsync</c>. It is shorter, but it cannot evolve a
/// schema, so the first change to this table in a real deployment would need the migration
/// machinery introduced under pressure. Migrations from the start cost one file.
/// </para>
/// </remarks>
public sealed class NotificationsDatabaseMigrator : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<NotificationsDatabaseOptions> _options;
    private readonly ILogger<NotificationsDatabaseMigrator> _logger;

    public NotificationsDatabaseMigrator(
        IServiceScopeFactory scopes,
        IOptions<NotificationsDatabaseOptions> options,
        ILogger<NotificationsDatabaseMigrator> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.MigrateOnStartup)
        {
            _logger.LogInformation(
                "Database:MigrateOnStartup is false; assuming notification_deliveries is already migrated.");
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        _logger.LogInformation("Applying ClaimPortal.Notifications migrations.");

        // EnableRetryOnFailure is configured on the context, so a Postgres container that is
        // accepting TCP connections but not yet ready is waited out here rather than crashing
        // the worker — which matters because compose starts both at the same time.
        await db.Database.MigrateAsync(cancellationToken);

        _logger.LogInformation("ClaimPortal.Notifications migrations are up to date.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

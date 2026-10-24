using ClaimPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClaimPortal.Api;

/// <summary>
/// Applies the API's migrations at startup so a clean `docker compose up` needs no manual step.
/// </summary>
public sealed class ClaimsDatabaseMigrator : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<DatabaseOptions> _options;
    private readonly ILogger<ClaimsDatabaseMigrator> _logger;

    public ClaimsDatabaseMigrator(
        IServiceScopeFactory scopes,
        IOptions<DatabaseOptions> options,
        ILogger<ClaimsDatabaseMigrator> logger)
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
                "Database:MigrateOnStartup is false; assuming the claims schema is already migrated.");
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();

        _logger.LogInformation("Applying ClaimPortal.Infrastructure migrations.");
        await db.Database.MigrateAsync(cancellationToken);
        _logger.LogInformation("ClaimPortal.Infrastructure migrations are up to date.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

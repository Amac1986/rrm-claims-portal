using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaimPortal.Notifications.Idempotency;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build a model without starting the worker.
/// </summary>
/// <remarks>
/// Without this, <c>dotnet ef</c> runs <c>Program.cs</c>, which starts the bus and tries to
/// reach RabbitMQ — so adding a migration would need a running broker. The connection string
/// here is a design-time placeholder; it is never connected to, because generating a migration
/// only needs the provider's SQL dialect. A real one would be a committed credential.
/// </remarks>
internal sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Database=claimportal_designtime;Username=postgres";

    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(
                DesignTimeConnectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    NotificationsDbContext.MigrationsHistoryTable, NotificationsDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new NotificationsDbContext(options);
    }
}

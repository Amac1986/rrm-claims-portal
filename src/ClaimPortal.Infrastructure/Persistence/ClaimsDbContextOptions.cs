using Microsoft.EntityFrameworkCore;

namespace ClaimPortal.Infrastructure.Persistence;

/// <summary>
/// The one place <see cref="ClaimsDbContext"/> options are configured.
/// </summary>
/// <remarks>
/// Shared by the runtime registration and by <see cref="ClaimsDbContextFactory"/>. If the
/// two configured the provider separately they could drift — most dangerously on the
/// naming convention, where design time would scaffold <c>claim_number</c> and runtime
/// would query <c>ClaimNumber</c>, and nothing would notice until a real query ran.
/// </remarks>
public static class ClaimsDbContextOptions
{
    /// <summary>Migration history table name, snake_cased to match everything else.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>Applies the Npgsql provider and the shared conventions.</summary>
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder builder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(MigrationsHistoryTable);
                npgsql.MigrationsAssembly(typeof(ClaimsDbContext).Assembly.FullName);
            })
            // snake_case throughout, including the MassTransit outbox tables. Postgres
            // folds unquoted identifiers to lower case, so PascalCase table names have to
            // be quoted in every hand-written query forever; snake_case does not.
            .UseSnakeCaseNamingConvention();
    }
}

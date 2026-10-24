using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ClaimPortal.Observability.HealthChecks;

/// <summary>
/// Readiness probe for PostgreSQL. Opens a real connection and runs <c>SELECT 1</c>; a
/// hard-coded healthy response would be the bug this task exists to prevent.
/// </summary>
public sealed class PostgresHealthCheck(string connectionString) : IHealthCheck
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A readiness probe must answer promptly while a dependency is down, so use a
            // probe-local 3s connect/command timeout instead of Npgsql's 15s default.
            var probeConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Timeout = 3,
                CommandTimeout = 3,
            }.ConnectionString;

            await using var connection = new NpgsqlConnection(probeConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand("SELECT 1", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return result is 1
                ? HealthCheckResult.Healthy("PostgreSQL answered SELECT 1.")
                : HealthCheckResult.Unhealthy("PostgreSQL answered with an unexpected value.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception type and message are useful; the connection string (which carries
            // the password) is deliberately never included.
            return HealthCheckResult.Unhealthy(
                $"PostgreSQL probe failed: {ex.GetType().Name}.",
                ex);
        }
    }
}

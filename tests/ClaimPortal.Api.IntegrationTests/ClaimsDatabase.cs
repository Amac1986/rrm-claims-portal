using ClaimPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaimPortal.Api.IntegrationTests;

/// <summary>
/// Direct database access for assertions and seeding.
/// </summary>
/// <remarks>
/// Assertions deliberately open their own <see cref="ClaimsDbContext"/> rather than reusing
/// the API's: a row that only exists in the API's change tracker has not landed, and the
/// point of these tests is the committed state in Postgres.
/// </remarks>
internal static class ClaimsDatabase
{
    public static ClaimsDbContext CreateContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<ClaimsDbContext>();
        ClaimsDbContextOptions.Configure(builder, connectionString);
        return new ClaimsDbContext(builder.Options);
    }

    public static async Task<List<string>> QueryStringsAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public static async Task<long> CountAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{table}\"", connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }
}

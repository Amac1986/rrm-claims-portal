using Npgsql;

namespace ClaimPortal.Api.IntegrationTests;

/// <summary>
/// A brand-new, empty database inside the shared PostgreSQL container.
/// </summary>
/// <remarks>
/// A database per test, rather than a container per test, keeps startup cost to one
/// container while still giving every test the "migrations from scratch against an empty
/// database" property. It also means a test cannot observe rows another test left behind.
/// The databases are discarded with the container, so nothing survives a run.
/// </remarks>
internal sealed class TestDatabase
{
    private const string AdministrativeDatabase = "postgres";

    private TestDatabase(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(ClaimPortalStack stack, CancellationToken cancellationToken)
    {
        // The fixture's account is the container superuser, so it may create databases.
        var administrative = new NpgsqlConnectionStringBuilder(stack.Postgres.GetConnectionString())
        {
            Database = AdministrativeDatabase,
        };

        var name = $"claims_test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(administrative.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var builder = new NpgsqlConnectionStringBuilder(stack.Postgres.GetConnectionString())
        {
            Database = name,
        };

        return new TestDatabase(builder.ConnectionString);
    }
}

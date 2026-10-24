using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaimPortal.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a <see cref="ClaimsDbContext"/> without starting the API.
/// </summary>
/// <remarks>
/// Migrations live in this project, so the tooling needs a way to construct the context
/// from here. The alternative — pointing <c>dotnet ef</c> at the API's host — means every
/// migration command boots the web application, its configuration and whatever the bus is
/// wired to, which is both slow and a way to have design-time tooling fail because a
/// broker is down.
/// </remarks>
public sealed class ClaimsDbContextFactory : IDesignTimeDbContextFactory<ClaimsDbContext>
{
    /// <summary>Environment variable holding the design-time connection string.</summary>
    public const string ConnectionStringVariable = "CLAIMPORTAL_DB";

    /// <summary>Connection string used when the environment variable is not set.</summary>
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=claimportal;Username=postgres;Password=postgres";

    /// <inheritdoc />
    public ClaimsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            args is [var fromArgs, ..] && !string.IsNullOrWhiteSpace(fromArgs) ? fromArgs
            : Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } fromEnv ? fromEnv
            : DefaultConnectionString;

        var builder = new DbContextOptionsBuilder<ClaimsDbContext>();
        ClaimsDbContextOptions.Configure(builder, connectionString);

        return new ClaimsDbContext(builder.Options);
    }
}

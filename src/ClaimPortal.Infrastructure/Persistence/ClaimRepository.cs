using ClaimPortal.Domain.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaimPortal.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IClaimRepository"/>.
/// </summary>
public sealed class ClaimRepository(ClaimsDbContext dbContext) : IClaimRepository
{
    /// <summary>Postgres SQLSTATE for <c>unique_violation</c>.</summary>
    private const string UniqueViolation = "23505";

    /// <inheritdoc />
    public async Task AddAsync(Claim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        dbContext.Claims.Add(claim);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsClaimNumberCollision(ex))
        {
            // Translated here so nothing above Infrastructure has to know what a
            // SQLSTATE is. Matching on the index name rather than on "some unique
            // violation happened" matters: a duplicate claim number is retryable, and
            // any other unique violation this table grows later would not be.
            throw new ClaimNumberCollisionException(claim.ClaimNumber, ex);
        }
    }

    private static bool IsClaimNumberCollision(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: UniqueViolation,
            ConstraintName: ClaimConfiguration.ClaimNumberIndexName,
        };
}

using ClaimPortal.Domain.Claims;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace ClaimPortal.Infrastructure.Persistence;

/// <summary>
/// The claims database. Holds both the claim tables and the MassTransit transactional
/// outbox tables.
/// </summary>
/// <remarks>
/// One <see cref="DbContext"/> for both is the whole point of the outbox: the claim row
/// and the <c>ClaimSubmitted</c> message row are written by a single
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> in a single transaction,
/// so there is no window in which a claim exists without its event or an event exists
/// without its claim. A second context, or a second database, would reintroduce exactly
/// the dual-write problem the outbox is here to remove.
/// </remarks>
public sealed class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    /// <summary>Submitted claims.</summary>
    public DbSet<Claim> Claims => Set<Claim>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClaimsDbContext).Assembly);

        // MassTransit's EF Core outbox schema. The API publishes through the bus outbox
        // (OutboxMessage/OutboxState); InboxState is MassTransit's consumer-side
        // deduplication table and is included because the entities ship and migrate as a
        // set — leaving it out means another migration the first time anything in this
        // service consumes a message.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}

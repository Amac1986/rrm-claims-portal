using ClaimPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ClaimPortal.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class MigrationsTests(ClaimPortalStack stack)
{
    private const string InitialMigration = "20261024015554_InitialClaims";

    [Fact]
    public async Task Migrations_apply_cleanly_to_an_empty_database_and_are_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(stack, cancellationToken);

        await using var db = ClaimsDatabase.CreateContext(database.ConnectionString);

        // A database created by the container is empty, so the first migration would have
        // to create everything. That is the "no works-only-on-my-machine drift" property.
        (await db.Database.GetPendingMigrationsAsync(cancellationToken))
            .ShouldContain(InitialMigration);

        await db.Database.MigrateAsync(cancellationToken);

        (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ShouldBeEmpty();
        (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ShouldHaveSingleItem()
            .ShouldBe(InitialMigration);

        // Running it a second time must be a no-op, not a partial apply or an error.
        await db.Database.MigrateAsync(cancellationToken);

        var tables = await ClaimsDatabase.QueryStringsAsync(
            database.ConnectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'",
            cancellationToken);

        tables.ShouldContain("claims");
        tables.ShouldContain("outbox_message");
        tables.ShouldContain("outbox_state");
        tables.ShouldContain("inbox_state");

        var indexes = await ClaimsDatabase.QueryStringsAsync(
            database.ConnectionString,
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'claims'",
            cancellationToken);

        // The unique constraint is the authority on claim-number uniqueness; the generator
        // and the retry only keep it from being hit.
        indexes.ShouldContain("ix_claims_claim_number");

        var history = await ClaimsDatabase.QueryStringsAsync(
            database.ConnectionString,
            "SELECT migration_id FROM \"__ef_migrations_history\" ORDER BY migration_id",
            cancellationToken);

        history.ShouldHaveSingleItem().ShouldBe(InitialMigration);
    }
}

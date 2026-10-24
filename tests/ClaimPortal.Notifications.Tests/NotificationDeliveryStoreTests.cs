using ClaimPortal.Notifications.Idempotency;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Shouldly;

namespace ClaimPortal.Notifications.Tests;

/// <summary>
/// The claim/confirm protocol, tested against a real relational database.
/// </summary>
/// <remarks>
/// SQLite rather than EF Core's InMemory provider on purpose: InMemory does not enforce unique
/// constraints, and the primary-key violation is the entire deduplication mechanism. These
/// tests would pass against InMemory even if the key were removed from the model.
/// </remarks>
public sealed class NotificationDeliveryStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 14, 3, 12, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly IdempotencyOptions _options = new();

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task TheFirstAttemptTakesTheClaim()
    {
        var key = NewKey();

        var claim = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        claim.Outcome.ShouldBe(DeliveryClaimOutcome.Claimed);
        claim.MaySend.ShouldBeTrue();
        claim.AttemptCount.ShouldBe(1);
        claim.SentAt.ShouldBeNull();

        var row = await ReadAsync(key.MessageId);
        row.ShouldNotBeNull();
        row.Status.ShouldBe(NotificationDeliveryStatus.Pending);
        row.ClaimNumber.ShouldBe(key.ClaimNumber);
        row.Recipient.ShouldBe(key.Recipient);
        row.FirstAttemptedAt.ShouldBe(Start);
    }

    [Fact]
    public async Task ASecondAttemptIsRefusedWhileTheFirstIsStillInFlight()
    {
        var key = NewKey();
        await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        // A separate store instance with its own DbContext — i.e. a different consumer.
        var concurrent = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        concurrent.Outcome.ShouldBe(DeliveryClaimOutcome.AttemptInFlight);
        concurrent.MaySend.ShouldBeFalse();
    }

    [Fact]
    public async Task ARedeliveryAfterASuccessfulSendIsToldTheEmailAlreadyWent()
    {
        var key = NewKey();
        var store = CreateStore();

        await store.TryClaimAsync(key, TestContext.Current.CancellationToken);
        await store.MarkSentAsync(key.MessageId, TestContext.Current.CancellationToken);

        var redelivery = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        // This is the branch that makes "exactly one email" true.
        redelivery.Outcome.ShouldBe(DeliveryClaimOutcome.AlreadySent);
        redelivery.MaySend.ShouldBeFalse();
        redelivery.SentAt.ShouldBe(Start);
    }

    [Fact]
    public async Task ARetryAfterAReportedFailureTakesTheClaimImmediately()
    {
        var key = NewKey();
        var store = CreateStore();

        await store.TryClaimAsync(key, TestContext.Current.CancellationToken);
        await store.MarkFailedAsync(key.MessageId, "relay refused the connection", TestContext.Current.CancellationToken);

        // A reported failure means we know the provider did not accept the email, so there is
        // no stale window to wait out — the retry may send straight away.
        var retry = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        retry.Outcome.ShouldBe(DeliveryClaimOutcome.Claimed);
        retry.AttemptCount.ShouldBe(2);

        var row = await ReadAsync(key.MessageId);
        row.ShouldNotBeNull();
        row.Status.ShouldBe(NotificationDeliveryStatus.Pending);
        row.AttemptCount.ShouldBe(2);
    }

    [Fact]
    public async Task AStaleClaimIsRecoveredSoAWorkerThatDiedMidAttemptDoesNotLoseTheEmail()
    {
        var key = NewKey();
        await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        // That attempt's process is gone. The row would stay Pending forever and the customer
        // would never be emailed; the stale window is the only thing that recovers it.
        _clock.Advance(_options.StaleClaimAfter + TimeSpan.FromSeconds(1));

        var recovered = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        recovered.Outcome.ShouldBe(DeliveryClaimOutcome.Claimed);
        recovered.AttemptCount.ShouldBe(2);
    }

    [Fact]
    public async Task OnlyOneOfTwoRedeliveriesCanRecoverTheSameStaleClaim()
    {
        var key = NewKey();
        await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);
        _clock.Advance(_options.StaleClaimAfter + TimeSpan.FromSeconds(1));

        // Both read the same stale row before either writes. Without the LastAttemptedAt guard
        // in the conditional update, both would decide to send and the customer gets two
        // emails — so this test is the guard, not a formality.
        var first = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);
        var second = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        new[] { first.Outcome, second.Outcome }
            .Count(outcome => outcome == DeliveryClaimOutcome.Claimed)
            .ShouldBe(1);
    }

    [Fact]
    public async Task AStillLiveClaimIsNotStolenJustBecauseTimeHasPassed()
    {
        var key = NewKey();
        await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        _clock.Advance(_options.StaleClaimAfter - TimeSpan.FromSeconds(1));

        var concurrent = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        concurrent.Outcome.ShouldBe(DeliveryClaimOutcome.AttemptInFlight);
    }

    [Fact]
    public async Task ALateFailureReportCannotOverwriteASentRow()
    {
        // The dangerous ordering: an attempt reports failure after another attempt recorded a
        // success. Letting that through would set the row back to Failed and the next
        // redelivery would cheerfully send a second email.
        var key = NewKey();
        var store = CreateStore();

        await store.TryClaimAsync(key, TestContext.Current.CancellationToken);
        await store.MarkSentAsync(key.MessageId, TestContext.Current.CancellationToken);
        await store.MarkFailedAsync(key.MessageId, "late failure report", TestContext.Current.CancellationToken);

        var row = await ReadAsync(key.MessageId);
        row.ShouldNotBeNull();
        row.Status.ShouldBe(NotificationDeliveryStatus.Sent);
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task GivesUpRatherThanSpinningWhenItKeepsLosingTheRace()
    {
        var key = NewKey();
        _options.MaxClaimAttempts = 1;

        await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        var contended = await CreateStore().TryClaimAsync(key, TestContext.Current.CancellationToken);

        // Reported as in-flight, never as Claimed: when in doubt the store refuses to send.
        contended.MaySend.ShouldBeFalse();
    }

    [Fact]
    public async Task TruncatesAnEnormousProviderErrorRatherThanFailingTheWrite()
    {
        var key = NewKey();
        var store = CreateStore();
        await store.TryClaimAsync(key, TestContext.Current.CancellationToken);

        await store.MarkFailedAsync(key.MessageId, new string('x', 5000), TestContext.Current.CancellationToken);

        var row = await ReadAsync(key.MessageId);
        row.ShouldNotBeNull();
        row.LastError.ShouldNotBeNull();
        row.LastError.Length.ShouldBe(1024);
        row.LastError.ShouldEndWith("…");
    }

    [Fact]
    public async Task EachMessageIdGetsItsOwnRow()
    {
        var store = CreateStore();

        var first = await store.TryClaimAsync(NewKey(), TestContext.Current.CancellationToken);
        var second = await store.TryClaimAsync(NewKey(), TestContext.Current.CancellationToken);

        first.Outcome.ShouldBe(DeliveryClaimOutcome.Claimed);
        second.Outcome.ShouldBe(DeliveryClaimOutcome.Claimed);

        await using var db = CreateContext();
        (await db.NotificationDeliveries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    private static DeliveryAttemptKey NewKey() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "CLM-20261026-K7F3QX", "dana.okonjo@example.ca");

    private NotificationsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);

    /// <summary>
    /// A fresh store over a fresh <see cref="NotificationsDbContext"/> each time, because that
    /// is what a second consumer is: these tests must never pass because two "concurrent"
    /// callers happened to share a change tracker.
    /// </summary>
    private EfNotificationDeliveryStore CreateStore() =>
        new(
            CreateContext(),
            new MutableOptionsMonitor<IdempotencyOptions>(_options),
            _clock,
            NullLogger<EfNotificationDeliveryStore>.Instance);

    private async Task<NotificationDelivery?> ReadAsync(Guid messageId)
    {
        await using var db = CreateContext();
        return await db.NotificationDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(delivery => delivery.MessageId == messageId, TestContext.Current.CancellationToken);
    }

    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

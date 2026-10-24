using System.Collections.Concurrent;
using ClaimPortal.Contracts;
using ClaimPortal.Notifications.Consuming;
using ClaimPortal.Notifications.Email;
using ClaimPortal.Notifications.Idempotency;
using ClaimPortal.Notifications.Messaging;
using ClaimPortal.Notifications.Templating;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ClaimPortal.Notifications.Tests.Support;

/// <summary>
/// Hosts <see cref="ClaimSubmittedConsumer"/> on MassTransit's in-memory test harness with the
/// real renderer, the real idempotency store over SQLite, and a scripted email sender.
/// </summary>
/// <remarks>
/// <para>
/// Everything except the transport and SMTP is the production object graph — in particular
/// <see cref="ClaimSubmittedConsumerDefinition"/> is registered, so the retry and delayed
/// redelivery policy under test is the same code that configures the RabbitMQ deployment. A
/// test that configured its own retry policy would prove nothing about the shipped one.
/// </para>
/// <para>
/// Needs no broker, no Postgres and no mail server, which is what lets the whole suite run on
/// a machine where Docker will not start.
/// </para>
/// </remarks>
internal sealed class NotificationWorkerHarness : IAsyncDisposable
{
    internal const string QueueName = "claim-submitted-notifications";

    /// <summary>MassTransit's per-endpoint dead-letter queue. Not a name we invent.</summary>
    internal const string ErrorQueueName = QueueName + "_error";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    private NotificationWorkerHarness(
        SqliteConnection connection,
        ServiceProvider provider,
        RecordingEmailSender sender,
        FakeTimeProvider clock,
        ConcurrentQueue<Guid> deadLettered,
        DeliveryClaimRecorder claims)
    {
        _connection = connection;
        _provider = provider;
        Sender = sender;
        Clock = clock;
        DeadLettered = deadLettered;
        Claims = claims;
        Harness = provider.GetRequiredService<ITestHarness>();
    }

    internal ITestHarness Harness { get; }

    internal RecordingEmailSender Sender { get; }

    internal FakeTimeProvider Clock { get; }

    /// <summary>
    /// Claim ids of messages observed arriving on <see cref="ErrorQueueName"/>. Populated by a
    /// receive endpoint bound to the error queue, so this is the real dead-letter path rather
    /// than an inference from a <see cref="Fault{T}"/> event.
    /// </summary>
    internal ConcurrentQueue<Guid> DeadLettered { get; }

    /// <summary>Every answer the idempotency store gave, in order.</summary>
    internal DeliveryClaimRecorder Claims { get; }

    internal static async Task<NotificationWorkerHarness> StartAsync(
        Func<int, Exception?>? sendBehaviour = null,
        Action<ResilienceOptions>? configureResilience = null,
        Action<IdempotencyOptions>? configureIdempotency = null,
        CancellationToken cancellationToken = default)
    {
        // Shared-cache in-memory SQLite, with the connection held open for the lifetime of the
        // harness: the database exists only as long as a connection to it does, and EF's
        // pooling would otherwise drop the schema between operations.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);

        var sender = new RecordingEmailSender { Behaviour = sendBehaviour ?? (_ => null) };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 14, 3, 12, TimeSpan.Zero));
        var deadLettered = new ConcurrentQueue<Guid>();
        var claims = new DeliveryClaimRecorder();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        services.AddOptions<SmtpOptions>();
        services.AddOptions<NotificationContentOptions>();

        services.AddOptions<IdempotencyOptions>()
            .Configure(options => configureIdempotency?.Invoke(options));

        services.AddOptions<MessagingOptions>()
            .Configure(options =>
            {
                options.Transport = MessagingTransport.InMemory;
                options.ClaimSubmittedQueueName = QueueName;

                // Sub-second intervals so the retry and redelivery paths are exercised in real
                // time instead of the five-minute production backoff. The *shape* of the policy
                // — which exception types are retried, in what order the filters run — comes
                // from ClaimSubmittedConsumerDefinition and is not overridden here.
                options.Resilience.RetryLimit = 3;
                options.Resilience.MinRetryInterval = TimeSpan.FromMilliseconds(1);
                options.Resilience.MaxRetryInterval = TimeSpan.FromMilliseconds(5);
                options.Resilience.RetryIntervalDelta = TimeSpan.FromMilliseconds(1);
                options.Resilience.RedeliveryIntervals = [];

                configureResilience?.Invoke(options.Resilience);
            });

        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IEmailSender>(sender);

        // The real Scriban renderer, not a stub: the harness tests assert that the claim number
        // reaches the email, which is only meaningful if the email is really rendered.
        services.AddSingleton<IClaimConfirmationRenderer, ScribanClaimConfirmationRenderer>();

        services.AddDbContext<NotificationsDbContext>(builder => builder
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention());

        // The real store, wrapped so the tests can see what it decided. See
        // RecordingDeliveryStore for why the harness's own message lists cannot be used here.
        services.AddSingleton(claims);
        services.AddScoped<EfNotificationDeliveryStore>();
        services.AddScoped<INotificationDeliveryStore>(provider => new RecordingDeliveryStore(
            provider.GetRequiredService<EfNotificationDeliveryStore>(),
            provider.GetRequiredService<DeliveryClaimRecorder>()));

        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<ClaimSubmittedConsumer, ClaimSubmittedConsumerDefinition>();

            bus.UsingInMemory((context, cfg) =>
            {
                cfg.ConfigureEndpoints(context);

                // Read the endpoint's own _error queue. This is what turns "the message
                // faulted" into "the message is sitting in the dead-letter queue".
                cfg.ReceiveEndpoint(ErrorQueueName, endpoint =>
                {
                    endpoint.ConfigureConsumeTopology = false;
                    endpoint.Handler<ClaimSubmitted>(consumeContext =>
                    {
                        deadLettered.Enqueue(consumeContext.Message.ClaimId);
                        return Task.CompletedTask;
                    });
                });
            });
        });

        var provider = services.BuildServiceProvider(validateScopes: true);

        await using (var scope = provider.CreateAsyncScope())
        {
            // EnsureCreated, not Migrate: the Npgsql migration cannot run on SQLite. The model
            // is the same one, so the primary key the deduplication depends on is real.
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        var harness = new NotificationWorkerHarness(connection, provider, sender, clock, deadLettered, claims);
        await harness.Harness.Start();

        return harness;
    }

    /// <summary>
    /// Publishes <paramref name="claim"/> with an explicit <c>MessageId</c>. Publishing the same
    /// id twice is how a broker redelivery is simulated — contract §2 guarantees the id is the
    /// outbox row id and therefore identical across redeliveries.
    /// </summary>
    internal Task PublishAsync(ClaimSubmitted claim, Guid messageId) =>
        Harness.Bus.Publish(claim, context => context.MessageId = messageId, Harness.CancellationToken);

    /// <summary>
    /// Publishes with no <c>MessageId</c> at all, which the consumer must refuse: without the
    /// idempotency key there is no way to tell a redelivery from a new claim.
    /// </summary>
    internal Task PublishWithoutMessageIdAsync(ClaimSubmitted claim) =>
        Harness.Bus.Publish(claim, context => context.MessageId = null, Harness.CancellationToken);

    internal async Task<NotificationDelivery?> GetDeliveryAsync(Guid messageId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.NotificationDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(delivery => delivery.MessageId == messageId, Harness.CancellationToken);
    }

    /// <summary>
    /// Waits until the consumer has asked the idempotency store to claim a message
    /// <paramref name="count"/> times.
    /// </summary>
    /// <remarks>
    /// Note this counts <em>attempts</em>, not messages: an in-process retry claims again, so a
    /// message retried twice contributes three. That is deliberate — it is the only wait that
    /// works for a redelivery, whose <c>MessageId</c> is by contract identical to the original
    /// and therefore invisible in the harness's own message lists.
    /// </remarks>
    internal Task WaitForClaimAttemptsAsync(int count, CancellationToken cancellationToken) =>
        WaitUntilAsync(
            () => Claims.Count >= count,
            $"the consumer to make {count} delivery-claim attempt(s)",
            cancellationToken);

    /// <summary>Waits until the mail sender has accepted <paramref name="count"/> emails.</summary>
    internal Task WaitForAcceptedEmailsAsync(int count, CancellationToken cancellationToken) =>
        WaitUntilAsync(
            () => Sender.Accepted.Count >= count,
            $"{count} email(s) to be accepted by the sender",
            cancellationToken);

    /// <summary>Waits until <paramref name="claimId"/> shows up on the <c>_error</c> queue.</summary>
    internal Task WaitForDeadLetterAsync(Guid claimId, CancellationToken cancellationToken) =>
        WaitUntilAsync(
            () => DeadLettered.Contains(claimId),
            $"claim {claimId} to arrive on {ErrorQueueName}",
            cancellationToken);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string description,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Fall through: the condition is re-checked once more before giving up, so a
                // state change that lands in the same tick as the timeout is not a flake.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

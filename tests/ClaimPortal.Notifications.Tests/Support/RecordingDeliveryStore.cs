using System.Collections.Concurrent;
using ClaimPortal.Notifications.Idempotency;

namespace ClaimPortal.Notifications.Tests.Support;

/// <summary>
/// Collects every answer the idempotency store gave. Singleton, because the store itself is
/// scoped — one per message — and the assertions need the whole sequence.
/// </summary>
internal sealed class DeliveryClaimRecorder
{
    private readonly ConcurrentQueue<DeliveryClaim> _claims = new();

    internal IReadOnlyList<DeliveryClaim> Claims => [.. _claims];

    internal int Count => _claims.Count;

    internal void Record(DeliveryClaim claim) => _claims.Enqueue(claim);
}

/// <summary>
/// Decorates the real <see cref="EfNotificationDeliveryStore"/> and records each
/// <see cref="INotificationDeliveryStore.TryClaimAsync"/> outcome.
/// </summary>
/// <remarks>
/// <para>
/// This exists because MassTransit's test harness indexes its recorded message lists by
/// <c>MessageId</c>, so a redelivery — which by contract §2 carries the <em>same</em> id —
/// collapses into a single entry and cannot be counted. Waiting on "the consumer has handled
/// two messages" is therefore not expressible through the harness for the one scenario that
/// matters most.
/// </para>
/// <para>
/// Observing the claim outcome is the better test anyway: instead of inferring deduplication
/// from a send that did not happen, the redelivery test asserts that the second attempt was
/// told <see cref="DeliveryClaimOutcome.AlreadySent"/>. The decorator adds no behaviour — the
/// decisions under test are all made by the real store against a real unique constraint.
/// </para>
/// </remarks>
internal sealed class RecordingDeliveryStore : INotificationDeliveryStore
{
    private readonly INotificationDeliveryStore _inner;
    private readonly DeliveryClaimRecorder _recorder;

    internal RecordingDeliveryStore(INotificationDeliveryStore inner, DeliveryClaimRecorder recorder)
    {
        _inner = inner;
        _recorder = recorder;
    }

    public async Task<DeliveryClaim> TryClaimAsync(DeliveryAttemptKey key, CancellationToken cancellationToken)
    {
        var claim = await _inner.TryClaimAsync(key, cancellationToken);
        _recorder.Record(claim);
        return claim;
    }

    public Task MarkSentAsync(Guid messageId, CancellationToken cancellationToken) =>
        _inner.MarkSentAsync(messageId, cancellationToken);

    public Task MarkFailedAsync(Guid messageId, string error, CancellationToken cancellationToken) =>
        _inner.MarkFailedAsync(messageId, error, cancellationToken);
}

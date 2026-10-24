using System.Threading.Channels;
using ClaimPortal.Api.Serialization;
using ClaimPortal.Contracts;
using MassTransit;

namespace ClaimPortal.Api.IntegrationTests;

/// <summary>
/// An independent MassTransit bus consuming <see cref="ClaimSubmitted"/> from the fixture's
/// real RabbitMQ container.
/// </summary>
/// <remarks>
/// This is deliberately not MassTransit's in-memory test harness. The assertion being made
/// is that the outbox delivery service actually put the message on a broker, over AMQP, to
/// a separate bus — the in-memory harness would pass even if the RabbitMQ transport were
/// misconfigured.
/// </remarks>
internal sealed class ClaimSubmittedProbe : IAsyncDisposable
{
    private readonly Channel<ConsumedClaim> _messages;
    private readonly IBusControl _bus;

    private ClaimSubmittedProbe(IBusControl bus, Channel<ConsumedClaim> messages)
    {
        _bus = bus;
        _messages = messages;
    }

    public static async Task<ClaimSubmittedProbe> StartAsync(
        ClaimPortalStack stack,
        CancellationToken cancellationToken)
    {
        var messages = Channel.CreateUnbounded<ConsumedClaim>();
        var queue = $"claim-submitted-probe-{Guid.NewGuid():N}";

        var bus = Bus.Factory.CreateUsingRabbitMq(configuration =>
        {
            configuration.ConfigureJsonSerializerOptions(ClaimPortalJson.Configure);
            configuration.Host(
                stack.RabbitMq.Hostname,
                (ushort)stack.RabbitMq.GetMappedPublicPort(ClaimPortalStack.RabbitMqPort),
                "/",
                host =>
                {
                    host.Username(ClaimPortalStack.RabbitMqUserName);
                    host.Password(ClaimPortalStack.RabbitMqPassword);
                });

            configuration.ReceiveEndpoint(queue, endpoint =>
            {
                endpoint.Handler<ClaimSubmitted>(context =>
                {
                    messages.Writer.TryWrite(new ConsumedClaim(context.Message, context.MessageId));
                    return Task.CompletedTask;
                });
            });
        });

        var probe = new ClaimSubmittedProbe(bus, messages);
        await bus.StartAsync(cancellationToken);
        return probe;
    }

    /// <summary>Waits for the next event, or fails the test on timeout.</summary>
    public async Task<ConsumedClaim> NextAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _messages.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);

    /// <summary>
    /// True if an event has arrived since the last read. Used to prove that a rolled-back
    /// attempt published nothing, after giving the outbox dispatcher time to have done so.
    /// </summary>
    public bool TryRead(out ConsumedClaim? claim) => _messages.Reader.TryRead(out claim);

    public async ValueTask DisposeAsync() => await _bus.StopAsync(CancellationToken.None);
}

internal sealed record ConsumedClaim(ClaimSubmitted Message, Guid? MessageId);

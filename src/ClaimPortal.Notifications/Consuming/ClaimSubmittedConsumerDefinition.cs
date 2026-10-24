using ClaimPortal.Notifications.Email;
using ClaimPortal.Notifications.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;

namespace ClaimPortal.Notifications.Consuming;

/// <summary>
/// The endpoint name and resilience policy for <see cref="ClaimSubmittedConsumer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both live in a <see cref="ConsumerDefinition{TConsumer}"/> rather than inline in
/// <c>Program.cs</c> for one concrete reason: <c>ConfigureEndpoints</c> applies a definition on
/// every transport, so the RabbitMQ deployment, the Azure Service Bus deployment and the
/// in-memory test harness all get the same retry and redelivery behaviour. A policy written at
/// the <c>UsingRabbitMq</c> call site would be a policy the tests never exercise.
/// </para>
/// <para><b>The three layers, outermost first:</b></para>
/// <list type="number">
///   <item><description>
///     <c>UseDelayedRedelivery</c> — for a relay that is down for minutes or hours. It returns
///     the message to the broker on a delay, so the consumer is free to process other claims
///     instead of holding a thread open.
///   </description></item>
///   <item><description>
///     <c>UseMessageRetry</c> — for a blip: a dropped connection or an SMTP 4xx. Retried
///     in-process over seconds, which is cheaper than a round trip through the broker.
///   </description></item>
///   <item><description>
///     Whatever is still failing after both lands on the <c>_error</c> queue, which MassTransit
///     creates and manages per endpoint. That is the dead-letter destination.
///   </description></item>
/// </list>
/// <para>
/// Ordering matters and is not arbitrary: filters run outside-in, so delayed redelivery must be
/// registered before message retry for "retry a few times quickly, then come back later" to be
/// what actually happens. Registered the other way round, the in-process retries would restart
/// on every redelivery and the effective attempt count would be the product of the two.
/// </para>
/// <para>
/// Both policies are scoped to <see cref="TransientEmailDeliveryException"/>.
/// <see cref="PermanentEmailDeliveryException"/> — a 5xx rejection, a bad recipient, a render
/// failure — is excluded on purpose, so it goes to <c>_error</c> on the first attempt instead
/// of after twenty minutes of retries that cannot succeed.
/// </para>
/// </remarks>
public sealed class ClaimSubmittedConsumerDefinition : ConsumerDefinition<ClaimSubmittedConsumer>
{
    private readonly ResilienceOptions _resilience;

    public ClaimSubmittedConsumerDefinition(IOptions<MessagingOptions> messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);

        _resilience = messaging.Value.Resilience;

        // Named explicitly rather than taking MassTransit's kebab-cased type name, because the
        // queue name is deployment surface: it appears in the broker, in dashboards and in the
        // Service Bus ARM/Bicep definitions, and it must not move when a class is renamed.
        EndpointName = messaging.Value.ClaimSubmittedQueueName;

        // One in-flight message per consumer by default. The work is a single SMTP round trip,
        // so concurrency buys little, and a low limit keeps a backlog from turning into a
        // thundering herd against the relay (or against a provider's rate limit) after an
        // outage. Configurable because the Service Bus deployment will want more.
        ConcurrentMessageLimit = messaging.Value.Resilience.ConcurrentMessageLimit;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ClaimSubmittedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        if (_resilience.RedeliveryIntervals.Count > 0)
        {
            // Requires transport-level delayed delivery. In-memory and Azure Service Bus have
            // it natively; RabbitMQ needs the rabbitmq_delayed_message_exchange plugin, which
            // is why the broker image is built rather than pulled, and why this is a
            // configurable list that can be emptied to fall back to retry + _error alone.
            endpointConfigurator.UseDelayedRedelivery(redelivery =>
            {
                redelivery.Handle<TransientEmailDeliveryException>();
                redelivery.Intervals([.. _resilience.RedeliveryIntervals]);
            });
        }

        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Handle<TransientEmailDeliveryException>();
            retry.Exponential(
                _resilience.RetryLimit,
                _resilience.MinRetryInterval,
                _resilience.MaxRetryInterval,
                _resilience.RetryIntervalDelta);
        });
    }
}

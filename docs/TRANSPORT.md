# The transport switch

The broker is a deployment concern. One configuration value moves both deployables between
the local RabbitMQ container and Azure Service Bus, and nothing in the consumer, the contract
or the outbox changes.

| `Messaging:Transport` | Where it is used | Broker |
| --- | --- | --- |
| `RabbitMq` (default) | `docker compose up`, `dotnet run` with Docker | Local RabbitMQ, including the delayed-message plugin |
| `AzureServiceBus` | The Azure Container Apps deployment | A Service Bus namespace, Standard tier |
| `InMemory` | Tests and a broker-less developer run | No broker; the process starts and consumes nothing |

The same value is read by both processes:

- API, publish side: `src/ClaimPortal.Api/Program.cs`
- Worker, consume side: `src/ClaimPortal.Notifications/NotificationsServiceCollectionExtensions.cs`

The change is in the registration only. `ClaimSubmitted`, `ClaimSubmittedConsumer`,
`ClaimSubmittedConsumerDefinition`, the EF outbox and the idempotency store are untouched.

## Configuration

```text
Messaging__Transport=AzureServiceBus
Messaging__AzureServiceBus__ConnectionString=<from the servicebus-connection-string secret>
# or, with a managed identity and no stored secret:
Messaging__AzureServiceBus__FullyQualifiedNamespace=<namespace>.servicebus.windows.net
```

The connection string is never committed. In the Bicep deployment it is a Container Apps
secret referenced by name (`servicebus-connection-string`) and injected as an environment
variable. The namespace form authenticates with `DefaultAzureCredential`; the identity needs
the **Azure Service Bus Data Owner** role so MassTransit can create topics and subscriptions.

If `AzureServiceBus` is selected and neither setting is present, startup fails with an
explicit error. It does not fall back to RabbitMQ: a silent fallback would look like a
working cloud deployment while publishing nowhere.

Local defaults are unchanged. `docker-compose.yml` sets `Messaging__Transport=RabbitMq`, so
`docker compose up` needs no Azure account.

## What the switch does and does not abstract

**Identical on both transports**

- The `ClaimSubmitted` contract and its JSON (PascalCase enums, UTC timestamps).
- The consumer and its endpoint name, `claim-submitted-notifications`.
- The resilience policy in `ClaimSubmittedConsumerDefinition`: in-process retry, then
  delayed redelivery, then the error destination. `ConfigureEndpoints` applies the definition
  on every transport, which is why the policy is not written at the `UsingRabbitMq` call site.
- Idempotency: the transport `MessageId` is the outbox row id and the unique key in
  `notification_deliveries`. The outbox is transport-neutral.
- The publish-before-commit guarantee: the API's outbox writes the message in the same
  transaction as the claim row, on either broker.

**Different, by design**

- Topology. RabbitMQ uses a `ClaimSubmitted` exchange bound to the worker queue. Service Bus
  uses a `ClaimSubmitted` topic with a subscription that auto-forwards to the
  `claim-submitted-notifications` queue (the queue the Bicep pre-creates for KEDA). The names
  are the same because both sides derive them from the same message and endpoint types.
- Delayed redelivery. Service Bus schedules it natively. RabbitMQ needs the
  `rabbitmq_delayed_message_exchange` plugin, which is why `docker/rabbitmq` is a built image.
- The error destination. MassTransit moves a faulted message to `<queue>_error` on both
  transports by default. Service Bus *also* has a native dead-letter sub-queue, used by the
  broker for messages that expire, exceed `maxDeliveryCount`, or fail a filter. The Bicep sets
  `deadLetteringOnMessageExpiration: true` and `maxDeliveryCount: 10`. Unless the receive
  endpoint is explicitly configured with `ConfigureDeadLetterQueueErrorTransport`, exhausted
  MassTransit retries still land in `_error`, not in the native dead-letter queue. Inspect both
  in the portal.
- Operational surface: management UI and `rabbitmqctl` versus the Service Bus Explorer, Azure
  Monitor and KEDA queue-depth scaling.

**Not abstracted**

- Ordering, sessions, partition keys, message size limits, throughput quotas and the
  authentication model. None of them is used by this flow; a future feature that needs them
  is a broker decision, not a configuration value.
- Exactly-once delivery. Neither broker provides it end to end; the outbox and the
  idempotency table are what make the flow safe.
- Consumer-side topology pre-creation. The Bicep creates the queue so KEDA can measure it;
  MassTransit creates the topic and subscription on first start.

## Verification status

- Both projects build with the transport registered; the existing unit and component suites
  pass unchanged.
- Selecting `AzureServiceBus` without credentials fails with the explicit configuration error;
  it is not a silent RabbitMQ fallback.
- **Verified against a live Azure Service Bus namespace.** The deployed stack runs with
  `Messaging:Transport=AzureServiceBus`; a claim submitted through the public web origin was
  persisted, published to the real namespace, consumed by the KEDA-scaled worker and delivered
  to the live Mailpit with its claim number. The topic/subscription topology and the
  queue-depth scaling therefore work as designed.
- **Not verified:** an induced broker outage (so `_error` filling and draining has not been
  observed on a real namespace), and the Service Bus emulator, which was not used.

using System.ComponentModel.DataAnnotations;

namespace ClaimPortal.Notifications.Messaging;

/// <summary>Which broker to talk to.</summary>
public enum MessagingTransport
{
    /// <summary>
    /// No broker at all. Used by the tests and by <c>dotnet run</c> on a machine with no
    /// Docker; the worker starts, logs and is healthy, it just never receives anything.
    /// </summary>
    InMemory = 0,

    /// <summary>The local and self-hosted default.</summary>
    RabbitMq = 1,

    /// <summary>
    /// The Azure deployment. Wired up by the Service Bus task; this member exists now so the
    /// swap is a value in <c>Messaging__Transport</c> and not a code change here.
    /// </summary>
    AzureServiceBus = 2,
}

/// <summary>
/// Broker configuration. There is no hard-coded endpoint anywhere in this assembly — a
/// reviewer running <c>docker compose up</c> and an operator pointing the worker at a managed
/// broker both do it through these keys.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public MessagingTransport Transport { get; set; } = MessagingTransport.RabbitMq;

    /// <summary>
    /// Queue the consumer binds to. Must match nothing in the API — the API publishes to the
    /// <c>ClaimSubmitted</c> exchange and knows no queue names, which is what lets a second
    /// consumer be added later without touching it.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ClaimSubmittedQueueName { get; set; } = "claim-submitted-notifications";

    public RabbitMqOptions RabbitMq { get; set; } = new();

    public AzureServiceBusOptions AzureServiceBus { get; set; } = new();

    public ResilienceOptions Resilience { get; set; } = new();
}

/// <summary>
/// RabbitMQ connection settings.
/// </summary>
/// <remarks>
/// <see cref="Password"/> never appears in <c>appsettings*.json</c> outside Development. In a
/// real deployment it arrives as <c>Messaging__RabbitMq__Password</c> from a container or
/// pipeline secret.
/// </remarks>
public sealed class RabbitMqOptions
{
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    [Required(AllowEmptyStrings = false)]
    public string VirtualHost { get; set; } = "/";

    [Required(AllowEmptyStrings = false)]
    public string UserName { get; set; } = "guest";

    [Required(AllowEmptyStrings = false)]
    public string Password { get; set; } = "guest";

    public bool UseSsl { get; set; }
}

/// <summary>
/// Azure Service Bus settings, consumed by the Service Bus task.
/// </summary>
/// <remarks>
/// Both members are optional and empty by default so that nothing here can make a local
/// <c>docker compose up</c> depend on an Azure account. <see cref="FullyQualifiedNamespace"/>
/// with managed identity is the intended production path;
/// <see cref="ConnectionString"/> exists for a developer with a personal namespace and is
/// never committed.
/// </remarks>
public sealed class AzureServiceBusOptions
{
    public string? FullyQualifiedNamespace { get; set; }

    public string? ConnectionString { get; set; }
}

/// <summary>
/// Retry, redelivery and concurrency numbers. All configuration rather than constants so an
/// operator can widen the window during a known relay outage without a redeploy.
/// </summary>
public sealed class ResilienceOptions
{
    [Range(1, 32)]
    public int ConcurrentMessageLimit { get; set; } = 1;

    /// <summary>Immediate in-process retries for a transient SMTP failure.</summary>
    [Range(0, 20)]
    public int RetryLimit { get; set; } = 5;

    public TimeSpan MinRetryInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryIntervalDelta { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Delays applied by <c>UseDelayedRedelivery</c> once the in-process retries are spent.
    /// Five minutes, fifteen, then an hour: roughly ninety minutes of tolerance for a mail
    /// relay outage before the message dead-letters, which is well inside the "contact you
    /// within one business day" promise the email itself makes.
    /// Empty disables redelivery entirely, for a broker with no delayed-delivery support.
    /// </summary>
    public IList<TimeSpan> RedeliveryIntervals { get; set; } =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
    ];
}

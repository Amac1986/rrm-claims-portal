using System.ComponentModel.DataAnnotations;

namespace ClaimPortal.Api.Messaging;

/// <summary>Which broker the API publishes to.</summary>
public enum MessagingTransport
{
    /// <summary>No broker. Used by tests and by `dotnet run` on a machine with no Docker.</summary>
    InMemory = 0,

    /// <summary>The local and self-hosted default.</summary>
    RabbitMq = 1,

    /// <summary>The Azure deployment. Selected by the same configuration value as locally.</summary>
    AzureServiceBus = 2,
}

/// <summary>Broker configuration. No endpoint is hard-coded in the assembly.</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public MessagingTransport Transport { get; set; } = MessagingTransport.RabbitMq;

    public RabbitMqOptions RabbitMq { get; set; } = new();

    public AzureServiceBusOptions AzureServiceBus { get; set; } = new();
}

/// <summary>
/// Azure Service Bus settings for the publish side. Both members are empty by default so a
/// local <c>docker compose up</c> can never require an Azure account.
/// </summary>
/// <remarks>
/// <see cref="FullyQualifiedNamespace"/> with <c>DefaultAzureCredential</c> is the intended
/// production path — no secret is stored. <see cref="ConnectionString"/> exists for a developer
/// with a personal namespace and for the current Bicep deployment, which passes it as a
/// Container Apps secret; it is never committed.
/// </remarks>
public sealed class AzureServiceBusOptions
{
    public string? FullyQualifiedNamespace { get; set; }

    public string? ConnectionString { get; set; }
}

/// <summary>RabbitMQ connection settings. The password is supplied by the environment.</summary>
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
}

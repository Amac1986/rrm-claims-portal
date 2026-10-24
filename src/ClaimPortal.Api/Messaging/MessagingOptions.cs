using System.ComponentModel.DataAnnotations;

namespace ClaimPortal.Api.Messaging;

/// <summary>Which broker the API publishes to.</summary>
public enum MessagingTransport
{
    /// <summary>No broker. Used by tests and by `dotnet run` on a machine with no Docker.</summary>
    InMemory = 0,

    /// <summary>The local and self-hosted default.</summary>
    RabbitMq = 1,

    /// <summary>Wired up by FIR-9; present now so the swap stays a configuration value.</summary>
    AzureServiceBus = 2,
}

/// <summary>Broker configuration. No endpoint is hard-coded in the assembly.</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public MessagingTransport Transport { get; set; } = MessagingTransport.RabbitMq;

    public RabbitMqOptions RabbitMq { get; set; } = new();
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

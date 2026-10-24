using Azure.Identity;
using MassTransit;

namespace ClaimPortal.Notifications.Messaging;

/// <summary>
/// Turns the Azure Service Bus section into a MassTransit host. Kept apart from the service
/// registration so the two authentication paths — a connection string or a namespace plus
/// <see cref="DefaultAzureCredential"/> — are in one place and the transport switch stays
/// readable.
/// </summary>
internal static class ServiceBusHostConfiguration
{
    /// <summary>
    /// The connection string wins when both are set: an operator who supplied one is explicitly
    /// overriding managed identity, which is also how the current Bicep deployment works.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Neither setting is present. Failing here is deliberate: silently selecting the local
    /// RabbitMQ transport would look like a working cloud deployment while consuming nothing.
    /// </exception>
    public static void Configure(IServiceBusBusFactoryConfigurator configurator, AzureServiceBusOptions options)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            configurator.Host(options.ConnectionString);
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace))
        {
            // Accept either the bare host from the portal or a full sb:// URI, so copying the
            // value from either place works and cannot produce sb://sb://....
            var namespaceAddress = options.FullyQualifiedNamespace.Trim();
            if (!namespaceAddress.Contains("://", StringComparison.Ordinal))
            {
                namespaceAddress = $"sb://{namespaceAddress}";
            }

            configurator.Host(new Uri(namespaceAddress.TrimEnd('/') + "/"), host =>
            {
                host.TokenCredential = new DefaultAzureCredential();
            });
            return;
        }

        throw new InvalidOperationException(
            "Messaging:Transport is AzureServiceBus but neither "
            + "Messaging:AzureServiceBus:ConnectionString nor "
            + "Messaging:AzureServiceBus:FullyQualifiedNamespace is set. Supply the connection "
            + "string as a container secret, or set the namespace and grant the workload "
            + "identity the Azure Service Bus Data Owner role.");
    }
}

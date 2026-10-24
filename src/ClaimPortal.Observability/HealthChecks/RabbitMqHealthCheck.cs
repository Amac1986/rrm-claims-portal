using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace ClaimPortal.Observability.HealthChecks;

/// <summary>RabbitMQ connection settings for the readiness probe.</summary>
public sealed class RabbitMqHealthOptions
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 5672;

    public string VirtualHost { get; init; } = "/";

    public string UserName { get; init; } = "guest";

    public string Password { get; init; } = "guest";

    public bool UseSsl { get; init; }
}

/// <summary>
/// Readiness probe for the broker. It opens and immediately closes a real AMQP connection, so
/// stopping RabbitMQ makes readiness fail instead of returning a stale 200.
/// </summary>
public sealed class RabbitMqHealthCheck(RabbitMqHealthOptions options) : IHealthCheck
{
    private readonly RabbitMqHealthOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            SocketReadTimeout = TimeSpan.FromSeconds(3),
            SocketWriteTimeout = TimeSpan.FromSeconds(3),
            AutomaticRecoveryEnabled = false,
        };

        if (_options.UseSsl)
        {
            factory.Ssl.Enabled = true;
        }

        try
        {
            await using var connection = await factory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await connection.CloseAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("RabbitMQ accepted an AMQP connection.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(
                $"RabbitMQ probe failed: {ex.GetType().Name}.",
                ex);
        }
    }
}

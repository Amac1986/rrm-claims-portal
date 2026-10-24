using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace ClaimPortal.Observability;

/// <summary>
/// The two processes' shared observability setup: compact JSON Serilog and an OTLP trace
/// exporter with a key-less local fallback.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>Standard OTel variable, honoured when no Honeycomb key is present.</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Secret key. Never committed; supplied by a container or pipeline secret.</summary>
    public const string HoneycombApiKeyVariable = "HONEYCOMB_API_KEY";

    private const string HoneycombEndpoint = "https://api.honeycomb.io:443";
    private const string MassTransitActivitySource = "MassTransit";

    /// <summary>
    /// Adds Serilog (structured JSON to stdout) and OpenTelemetry tracing to a host.
    /// </summary>
    /// <param name="builder">The API or worker host builder.</param>
    /// <param name="defaultServiceName">Used for <c>service.name</c> unless configured.</param>
    /// <param name="configureTracing">
    /// Adds technology-specific instrumentation (for example ASP.NET Core and HttpClient in the
    /// API). The worker only needs the MassTransit activity source, which is always added.
    /// </param>
    public static IHostApplicationBuilder AddClaimPortalObservability(
        this IHostApplicationBuilder builder,
        string defaultServiceName,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        var configuration = builder.Configuration;
        var enabled = configuration.GetValue("Observability:Enabled", defaultValue: true);
        var serviceName = configuration["Observability:ServiceName"];
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = configuration["OTEL_SERVICE_NAME"];
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = defaultServiceName;
        }

        var environmentName = configuration["Observability:Environment"];
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            environmentName = builder.Environment.EnvironmentName;
        }

        var serviceVersion = ResolveServiceVersion();

        if (enabled)
        {
            builder.Services.AddSerilog((_, logger) => ConfigureLogging(
                logger,
                serviceName,
                environmentName));
        }

        if (!enabled)
        {
            // Tests and local ad-hoc runs can turn the exporter off. Health checks and logging
            // stay available; only the background OTLP connection is skipped.
            return builder;
        }

        var (endpoint, honeycombKey) = ResolveOtlpEndpoint(configuration);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: $"{Environment.MachineName}-{Environment.ProcessId}")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = environmentName,
                }))
            .WithTracing(tracing =>
            {
                // MassTransit 8 publishes its own ActivitySource and injects the W3C trace
                // context into message headers, which is what makes the consumer span a child
                // of the API's request span instead of an orphan trace.
                tracing.AddSource(MassTransitActivitySource);

                configureTracing?.Invoke(tracing);

                tracing.AddOtlpExporter(options =>
                {
                    options.Endpoint = endpoint;
                    options.Protocol = OtlpExportProtocol.Grpc;
                    options.TimeoutMilliseconds = 5_000;

                    if (honeycombKey is not null)
                    {
                        options.Headers = $"x-honeycomb-team={honeycombKey}";
                    }
                });
            });

        return builder;
    }

    /// <summary>
    /// A Honeycomb key always wins: the app then exports straight to Honeycomb. Without one,
    /// the standard OTel endpoint is used, and in compose that points at the bundled collector.
    /// A clone with no account and no key therefore still produces traces.
    /// </summary>
    private static (Uri Endpoint, string? HoneycombKey) ResolveOtlpEndpoint(
        IConfiguration configuration)
    {
        var honeycombKey = configuration[HoneycombApiKeyVariable];
        if (!string.IsNullOrWhiteSpace(honeycombKey))
        {
            return (new Uri(HoneycombEndpoint), honeycombKey.Trim());
        }

        var configured = configuration[OtlpEndpointVariable];
        var endpoint = string.IsNullOrWhiteSpace(configured)
            ? "http://localhost:4317"
            : configured.Trim();

        return (new Uri(endpoint), null);
    }

    private static void ConfigureLogging(
        LoggerConfiguration logger,
        string serviceName,
        string environmentName)
    {
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.With<ActivityLogEnricher>()
            .Enrich.WithProperty("service.name", serviceName)
            .Enrich.WithProperty("deployment.environment", environmentName)
            .WriteTo.Console(new CompactJsonFormatter());
    }

    private static string ResolveServiceVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ObservabilityExtensions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}

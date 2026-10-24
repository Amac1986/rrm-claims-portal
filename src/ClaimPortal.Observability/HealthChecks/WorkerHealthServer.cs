using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaimPortal.Observability.HealthChecks;

/// <summary>
/// The worker's equivalent of the API's <c>/health/live</c> and <c>/health/ready</c>.
/// </summary>
/// <remarks>
/// The worker is a Worker SDK process, so it has no HTTP server. Rather than pull the whole
/// ASP.NET Core stack (and a larger runtime image) into a service that only consumes messages,
/// this hosted service listens on a small HTTP port with <see cref="HttpListener"/>. Compose
/// and Container Apps can therefore probe the worker exactly like the API, and readiness runs
/// the registered PostgreSQL and broker checks for real.
/// </remarks>
public sealed class WorkerHealthServer : BackgroundService
{
    private readonly HealthCheckService _healthChecks;
    private readonly ILogger<WorkerHealthServer> _logger;
    private readonly int _port;
    private HttpListener? _listener;

    public WorkerHealthServer(
        HealthCheckService healthChecks,
        IConfiguration configuration,
        ILogger<WorkerHealthServer> logger)
    {
        _healthChecks = healthChecks;
        _logger = logger;
        _port = configuration.GetValue("Observability:HealthPort", defaultValue: 8081);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // On Linux (containers) bind all interfaces so a pod or the Docker network can reach
        // the probe. On Windows a wildcard prefix needs an URL ACL, so local development uses
        // loopback instead; the container path is the one that matters for probes.
        var prefix = OperatingSystem.IsWindows()
            ? $"http://localhost:{_port}/"
            : $"http://*:{_port}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(
                ex,
                "Could not start the worker health endpoint on {Prefix}. Readiness probes will fail.",
                prefix);
            throw;
        }

        _logger.LogInformation(
            "Worker health endpoints listening on {Prefix} (/health/live, /health/ready).",
            prefix);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync()
                        .WaitAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = HandleAsync(context, stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken stoppingToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;

            if (path.Equals("/health/live", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(context, 200, new { status = "live" }, stoppingToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(context, 404, new { status = "not-found" }, stoppingToken)
                    .ConfigureAwait(false);
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var report = await _healthChecks
                .CheckHealthAsync(registration => registration.Tags.Contains("ready"), timeout.Token)
                .ConfigureAwait(false);

            var statusCode = report.Status == HealthStatus.Healthy ? 200 : 503;
            var body = new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString().ToLowerInvariant(),
                    description = entry.Value.Description,
                }),
                durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            };

            await WriteAsync(context, statusCode, body, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe must never take the worker down; answer 503 and let the next probe retry.
            _logger.LogWarning(ex, "Worker health probe failed while handling the request.");
            TryFail(context.Response);
        }
    }

    private static async Task WriteAsync(
        HttpListenerContext context,
        int statusCode,
        object body,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";

        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        await context.Response.OutputStream
            .WriteAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        context.Response.Close();
    }

    private static void TryFail(HttpListenerResponse response)
    {
        try
        {
            response.StatusCode = 503;
            response.ContentType = "application/json; charset=utf-8";
            response.Close();
        }
        catch (HttpListenerException)
        {
            // The probe client hung up; nothing left to do.
        }
    }
}

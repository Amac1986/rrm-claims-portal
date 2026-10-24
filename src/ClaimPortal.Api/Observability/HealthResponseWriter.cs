using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClaimPortal.Api.Observability;

/// <summary>
/// A small, stable JSON shape for both API health endpoints. The shared name/status/description
/// fields match the worker's endpoint, so one dashboard or script can consume either process.
/// </summary>
public static class HealthResponseWriter
{
    public static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString().ToLowerInvariant(),
                description = entry.Value.Description,
            }),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
        });
    }
}

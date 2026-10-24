using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace ClaimPortal.Observability;

/// <summary>
/// Adds the active W3C trace and span ids to every Serilog event, in both processes.
/// </summary>
/// <remarks>
/// The trace id is what ties the API's JSON log line to the worker's for one claim, and the
/// span id identifies the precise operation. Serilog has no built-in enricher for
/// <see cref="Activity"/> unless another package is added; twenty lines avoids that dependency.
/// </remarks>
public sealed class ActivityLogEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("TraceId", activity.TraceId.ToHexString()));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("SpanId", activity.SpanId.ToHexString()));
    }
}

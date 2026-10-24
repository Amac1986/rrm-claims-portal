using System.Diagnostics;

namespace ClaimPortal.Observability;

/// <summary>
/// The correlation id for the current async flow.
/// </summary>
/// <remarks>
/// An <see cref="AsyncLocal{T}"/> rather than an <c>IHttpContextAccessor</c>: the API writes
/// the outbox row inside the request, but MassTransit may dispatch or retry the message from a
/// background task. AsyncLocal follows the logical call and lets the publish callback stamp
/// the message header without the observability layer taking a dependency on ASP.NET Core.
/// </remarks>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? Current => CurrentValue.Value;

    public static IDisposable Push(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var previous = CurrentValue.Value;
        CurrentValue.Value = correlationId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}

/// <summary>
/// Header names and resolution rules shared by the API, the broker message and the worker.
/// </summary>
public static class CorrelationId
{
    /// <summary>The HTTP header and the MassTransit message header. Same name on both hops.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>The Serilog property name, so both services' JSON logs can be grepped with one key.</summary>
    public const string LogPropertyName = "CorrelationId";

    /// <summary>OpenTelemetry span attribute, visible in the trace waterfall.</summary>
    public const string TagName = "correlation.id";

    private const int MaxLength = 128;

    /// <summary>
    /// Uses the caller's id when it is safe to echo, otherwise derives one from the current
    /// trace (which is already unique and travels with the request) or creates a fresh id.
    /// </summary>
    public static string Resolve(string? supplied, Activity? activity = null)
    {
        if (IsSafe(supplied))
        {
            return supplied!.Trim();
        }

        var traceId = activity?.TraceId;
        if (traceId is { } id && id != default)
        {
            return id.ToHexString();
        }

        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Correlation ids are echoed into a response header and into JSON logs, so accept only a
    /// short token. Control characters and whitespace are rejected rather than sanitised:
    /// a malformed id is a caller bug, and silently rewriting it hides that.
    /// </summary>
    private static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/'))
            {
                return false;
            }
        }

        return true;
    }
}

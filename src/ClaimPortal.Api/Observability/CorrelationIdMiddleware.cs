using System.Diagnostics;
using ClaimPortal.Observability;
using Serilog.Context;

namespace ClaimPortal.Api.Observability;

/// <summary>
/// Establishes one correlation id per HTTP request before anything is logged or published.
/// </summary>
/// <remarks>
/// The id is accepted from a caller-supplied <c>X-Correlation-ID</c>, otherwise derived from
/// the W3C trace id ASP.NET Core's instrumentation created for the request. It is echoed on
/// the response, pushed into Serilog's log context, and read by the claim endpoint to stamp
/// the MassTransit message header. That is the same string in the API log, the broker message
/// header and the worker log.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = CorrelationId.Resolve(
            context.Request.Headers[CorrelationId.HeaderName].ToString(),
            Activity.Current);

        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationId.HeaderName] = correlationId;

        Activity.Current?.SetTag(CorrelationId.TagName, correlationId);

        using var correlationScope = CorrelationContext.Push(correlationId);
        using var logScope = LogContext.PushProperty(CorrelationId.LogPropertyName, correlationId);

        await next(context);
    }
}

using System.Diagnostics;

namespace Bitstream.Api.Middleware;

/// <summary>
/// TR-ARC-04: assigns a correlation ID to every request, echoes it on the response and puts
/// it in the logging scope so that every log entry written while handling the request
/// carries it. Adapters propagate the same value onto downstream calls (TR-INT-02).
/// </summary>
public sealed class CorrelationIdMiddleware
{
    /// <summary>Header carrying the correlation ID inbound and outbound.</summary>
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A caller-supplied ID is honoured so that a CRM-originated event can be traced
        // end to end; otherwise the trace identifier is used.
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        }

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }
}

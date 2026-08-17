using System.Diagnostics;

namespace Bitstream.Api.Middleware;

/// <summary>
/// One structured log entry per request, carrying method, route, status, duration and the
/// correlation ID established by <see cref="CorrelationIdMiddleware"/>.
/// <para>
/// TR-INT-09 requires integration traffic to be logged with outcome and duration and sensitive
/// fields masked; this is the request-side half of that, and the same discipline applies —
/// nothing here logs a payload, a query string or a header, because any of the three can carry
/// personal data (TR-NFR-20) or a token.
/// </para>
/// <para>
/// Duration is also what makes TR-NFR-02 (500 ms at the 95th percentile for reads) and
/// TR-INT-30 (2 seconds at the 95th percentile for the CRM inbound API) measurable rather than
/// aspirational.
/// </para>
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Health probes run every few seconds; logging them at Information buries everything
        // else. They are still logged when they fail, which is the case anyone looks for.
        var isProbe = context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            var failed = Stopwatch.GetElapsedTime(timestamp);

            // Logged here as well as by the exception handler, because this is the entry that
            // carries the duration and the route.
            _logger.LogError(
                exception,
                "HTTP {RequestMethod} {RequestPath} failed after {ElapsedMilliseconds:F1} ms",
                context.Request.Method,
                context.Request.Path.Value,
                failed.TotalMilliseconds);

            throw;
        }

        var elapsed = Stopwatch.GetElapsedTime(timestamp);
        var statusCode = context.Response.StatusCode;

        var level = statusCode switch
        {
            >= 500 => LogLevel.Error,
            // 401, 403 and 429 are security-relevant: TR-SEC-19 and TR-INT-23 require rejected
            // access to be logged as an event, not swallowed as routine traffic.
            401 or 403 or 429 => LogLevel.Warning,
            >= 400 => LogLevel.Information,
            _ => isProbe ? LogLevel.Debug : LogLevel.Information
        };

        _logger.Log(
            level,
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {ElapsedMilliseconds:F1} ms",
            context.Request.Method,
            context.Request.Path.Value,
            statusCode,
            elapsed.TotalMilliseconds);
    }
}

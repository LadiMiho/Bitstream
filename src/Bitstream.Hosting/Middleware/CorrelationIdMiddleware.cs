using System.Diagnostics;
using Bitstream.Application;
using Bitstream.Application.Abstractions;

namespace Bitstream.Hosting.Middleware;

/// <summary>
/// TR-ARC-04: assigns a correlation ID to every request, echoes it on the response, publishes it
/// as ambient state for the rest of the pipeline and puts it in the logging scope so that every
/// entry written while handling the request carries it.
/// <para>
/// A caller-supplied ID is honoured — that is what makes a CRM-originated event traceable from
/// CRM's logs through the portal's — but only when it is well formed. An arbitrary header value
/// would otherwise end up in log fields and in outbound payloads, which is both a log-injection
/// route and a way to break correlation by supplying the same ID for every call.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddleware
{
    /// <summary>Header carrying the correlation ID inbound and outbound.</summary>
    public const string HeaderName = CorrelationHeaders.Name;

    /// <summary>Upper bound on an accepted inbound correlation ID.</summary>
    public const int MaxLength = 64;

    private readonly RequestDelegate _next;
    private readonly ICorrelationContext _correlationContext;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(
        RequestDelegate next,
        ICorrelationContext correlationContext,
        ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _correlationContext = correlationContext;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = ResolveCorrelationId(context);

        context.Items[HeaderName] = correlationId;

        // Set before the response starts, because headers cannot be added afterwards and the
        // caller needs the ID to quote in a support request.
        context.Response.Headers[HeaderName] = correlationId;

        using var ambient = _correlationContext.BeginScope(correlationId);
        using var loggingScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
        });

        await _next(context);
    }

    /// <summary>
    /// Accepts a well-formed inbound ID, otherwise generates one. Exposed for testing the
    /// acceptance rule directly.
    /// </summary>
    /// <param name="context">Current request.</param>
    public static string ResolveCorrelationId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();

        return IsAcceptable(supplied)
            ? supplied!
            : Activity.Current?.TraceId.ToString() ?? CorrelationContext.NewCorrelationId();
    }

    /// <summary>
    /// A correlation ID must be short and printable: it is written into log fields, into
    /// outbound integration payloads and into the audit trail.
    /// </summary>
    /// <param name="value">Candidate value from the request header.</param>
    public static bool IsAcceptable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var acceptable = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':';

            if (!acceptable)
            {
                return false;
            }
        }

        return true;
    }
}

using System.Text.Json;
using Bitstream.Application.Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Api;

/// <summary>
/// Turns a request the endpoint could not even read — most often a body that is not valid JSON —
/// into the 4xx it is (usually 400), with the reader's own explanation in <c>detail</c>, e.g.
/// "'p' is an invalid start of a property name. Path: $.payload | LineNumber: 8". Without this,
/// the default exception handler answers such a caller mistake with an unexplained 500.
/// Every other exception is left to the default handler.
/// </summary>
public sealed partial class BadRequestExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ICorrelationContext _correlationContext;
    private readonly ILogger<BadRequestExceptionHandler> _logger;

    public BadRequestExceptionHandler(
        IProblemDetailsService problemDetails, ICorrelationContext correlationContext, ILogger<BadRequestExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _correlationContext = correlationContext;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        var detail = FirstOf<JsonException>(badRequest)?.Message ?? badRequest.Message;
        LogBadRequest(_logger, httpContext.Request.Method, httpContext.Request.Path, badRequest.StatusCode, detail);

        httpContext.Response.StatusCode = badRequest.StatusCode;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "The request body could not be read",
                Detail = detail,
                Extensions = { ["correlationId"] = _correlationContext.CorrelationId }
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The outermost <typeparamref name="T"/> in the chain: for JSON that is the serializer's
    /// exception, whose message carries the path (e.g. $.payload) as well as the line and position.
    /// </summary>
    private static T? FirstOf<T>(Exception exception)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Method} {Path} rejected with {StatusCode}: the request could not be read. {Detail}")]
    private static partial void LogBadRequest(ILogger logger, string method, PathString path, int statusCode, string detail);
}

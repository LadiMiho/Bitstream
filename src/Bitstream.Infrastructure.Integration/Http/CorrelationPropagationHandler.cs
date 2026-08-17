using System.Diagnostics;
using Bitstream.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Bitstream.Infrastructure.Integration.Http;

/// <summary>
/// Puts the current correlation ID on every outbound call and logs the outcome and duration of
/// each one.
/// <para>
/// TR-ARC-04 requires the correlation ID to be propagated to all downstream calls; TR-INT-02
/// requires every outbound message to carry it; TR-INT-09 requires integration traffic to be
/// logged with outcome and duration. Doing it in a message handler means no adapter can forget:
/// the requirement is satisfied by the pipeline rather than by each call site remembering.
/// </para>
/// <para>
/// Payloads are not logged here. TR-INT-09 asks for sensitive fields to be masked, and the
/// place that knows which fields are sensitive is the adapter, not a generic handler — so the
/// handler logs metadata only and payload logging belongs with the mapping code.
/// </para>
/// </summary>
public sealed class CorrelationPropagationHandler : DelegatingHandler
{
    private readonly ICorrelationContext _correlationContext;
    private readonly ILogger<CorrelationPropagationHandler> _logger;

    public CorrelationPropagationHandler(
        ICorrelationContext correlationContext,
        ILogger<CorrelationPropagationHandler> logger)
    {
        _correlationContext = correlationContext;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var correlationId = _correlationContext.CorrelationId;

        if (!request.Headers.Contains(CorrelationHeaders.Name))
        {
            request.Headers.TryAddWithoutValidation(CorrelationHeaders.Name, correlationId);
        }

        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            _logger.Log(
                response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning,
                "Outbound {RequestMethod} {RequestUri} returned {StatusCode} in {ElapsedMilliseconds:F1} ms",
                request.Method.Method,
                request.RequestUri,
                (int)response.StatusCode,
                elapsed.TotalMilliseconds);

            return response;
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancellation the caller did not ask for is the client timeout. It is worth
            // distinguishing in the log, because TR-INT-20 treats a timeout differently from a
            // failure: it must be followed by a status query or an idempotent retry, never a
            // blind second create.
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            _logger.LogWarning(
                exception,
                "Outbound {RequestMethod} {RequestUri} timed out after {ElapsedMilliseconds:F1} ms",
                request.Method.Method,
                request.RequestUri,
                elapsed.TotalMilliseconds);

            throw;
        }
        catch (HttpRequestException exception)
        {
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            _logger.LogWarning(
                exception,
                "Outbound {RequestMethod} {RequestUri} failed after {ElapsedMilliseconds:F1} ms",
                request.Method.Method,
                request.RequestUri,
                elapsed.TotalMilliseconds);

            throw;
        }
    }
}

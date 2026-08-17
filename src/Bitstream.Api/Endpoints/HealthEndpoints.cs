using System.Text.Json;
using Bitstream.Api.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bitstream.Api.Endpoints;

/// <summary>
/// TR-ARC-05: a health endpoint per service reporting the reachability of its dependencies —
/// database, CRM, BI and SMTP.
/// <para>
/// Two endpoints, because the two questions have different answers and different consequences:
/// </para>
/// <list type="bullet">
///   <item><c>/health/live</c> — is the process up. Consults nothing. IIS and any load balancer
///   watch this one, because TR-NFR-07 requires the portal to stay usable in read mode when
///   CRM or BI is unavailable: an integration outage must not cause a recycle.</item>
///   <item><c>/health/ready</c> — can the portal do its work. Runs every dependency check and
///   reports each one separately, which is what monitoring alerts on (TR-NFR-16).</item>
/// </list>
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Tag applied to checks that probe an external dependency.</summary>
    public const string DependencyTag = "dependency";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            // No check runs: liveness must not depend on anything the portal cannot control.
            Predicate = _ => false,
            ResponseWriter = WriteResponseAsync
        })
        .WithTags("Health")
        .WithName("HealthLive")
        .WithSummary("Liveness probe — process only, consults no dependency")
        .AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(DependencyTag),
            ResponseWriter = WriteResponseAsync
        })
        .WithTags("Health")
        .WithName("HealthReady")
        .WithSummary("Readiness probe — reports database, CRM, BI, SAP and SMTP reachability")
        .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Writes the per-dependency detail TR-ARC-05 asks for.
    /// <para>
    /// Exception detail is deliberately omitted. A failed database check's exception names the
    /// server and the database, and these endpoints are unauthenticated so that a load balancer
    /// can reach them; the detail is in the logs, which is where an operator should be looking
    /// anyway (TR-SEC-27).
    /// </para>
    /// </summary>
    /// <param name="context">Current request.</param>
    /// <param name="report">Aggregated health report.</param>
    public static async Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var id)
                ? id as string
                : null,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                data = entry.Value.Data.Count == 0 ? null : entry.Value.Data
            })
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, JsonOptions),
            context.RequestAborted);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

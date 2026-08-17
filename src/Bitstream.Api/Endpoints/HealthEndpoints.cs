namespace Bitstream.Api.Endpoints;

/// <summary>
/// TR-ARC-05: a health endpoint per service reporting the reachability of its dependencies —
/// database, CRM, BI and SMTP.
/// <para>
/// The endpoints are mapped now; the individual dependency checks are registered by each
/// adapter as it is built, so that a check is never claimed before the dependency it covers
/// actually exists.
/// </para>
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liveness: is the process up. No dependency is consulted, so a CRM outage never
        // causes IIS or the load balancer to recycle a healthy portal (TR-NFR-07).
        app.MapHealthChecks("/health/live", new()
        {
            Predicate = _ => false
        })
        .WithTags("Health")
        .WithName("HealthLive")
        .WithSummary("Liveness probe");

        // Readiness: all registered dependency checks.
        app.MapHealthChecks("/health/ready")
            .WithTags("Health")
            .WithName("HealthReady")
            .WithSummary("Readiness probe reporting dependency reachability");

        return app;
    }
}

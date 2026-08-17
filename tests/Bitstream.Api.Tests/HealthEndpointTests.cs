using System.Net;
using System.Text.Json;
using Bitstream.Api.Middleware;
using Xunit;

namespace Bitstream.Api.Tests;

/// <summary>
/// TR-ARC-05: a health endpoint per service reporting the reachability of its dependencies.
/// <para>
/// The dependencies are unreachable in these tests, which is the interesting case: what matters
/// is that liveness stays green while readiness reports each dependency separately. TR-NFR-07
/// requires the portal to remain usable in read mode when CRM or BI is unavailable, and a
/// liveness probe that fails on a CRM outage would have IIS recycle a working portal.
/// </para>
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<BitstreamApiFactory>
{
    private readonly BitstreamApiFactory _factory;

    public HealthEndpointTests(BitstreamApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Liveness_is_healthy_even_though_every_dependency_is_unreachable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Healthy", document.RootElement.GetProperty("status").GetString());

        // Liveness must consult nothing at all — an empty check list is the assertion.
        Assert.Empty(document.RootElement.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task Readiness_reports_every_dependency_separately()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var names = document.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("database", names);
        Assert.Contains("crm", names);
        Assert.Contains("bi", names);
        Assert.Contains("sap", names);
        Assert.Contains("smtp", names);
    }

    [Fact]
    public async Task Readiness_is_unavailable_while_the_database_is_unreachable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var database = document.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "database");

        Assert.Equal("Unhealthy", database.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_unconfigured_dependency_is_degraded_rather_than_unhealthy()
    {
        // The CRM contract is TRD 11.4 open item 1, so "not configured" is today's expected
        // state. Reporting it as Unhealthy would make readiness red everywhere and train
        // everyone to ignore the endpoint.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var crm = document.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "crm");

        Assert.Equal("Degraded", crm.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_disabled_sap_adapter_is_healthy()
    {
        // TR-INT-14: the absence of a financial code blocks nothing, so a switched-off adapter
        // is a correct state and must not colour readiness.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var sap = document.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "sap");

        Assert.Equal("Healthy", sap.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Every_check_reports_its_duration()
    {
        // TR-NFR-16 alerts on these; a status with no duration cannot show a slow dependency.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var check in document.RootElement.GetProperty("checks").EnumerateArray())
        {
            Assert.True(check.TryGetProperty("durationMs", out var duration));
            Assert.True(duration.GetDouble() >= 0);
        }
    }

    [Fact]
    public async Task Health_responses_carry_the_correlation_id()
    {
        // A support conversation about a failing probe starts from this value (TR-ARC-04).
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var header));
        Assert.False(string.IsNullOrWhiteSpace(header.Single()));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(header.Single(), document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Health_responses_do_not_leak_exception_detail()
    {
        // These endpoints are unauthenticated so the load balancer can reach them; a failed
        // database probe's exception names the server and the database (TR-SEC-27).
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("not-a-real-password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", body, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Diagnostics;
using System.Net.Sockets;
using Bitstream.Infrastructure.Integration.Bi;
using Bitstream.Infrastructure.Integration.Crm;
using Bitstream.Infrastructure.Integration.Mail;
using Bitstream.Infrastructure.Integration.Sap;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Integration.HealthChecks;

/*
 * Dependency reachability for TR-ARC-05.
 *
 * Three rules, applied consistently across all four checks:
 *
 *   1. Not configured is Degraded, not Unhealthy. The CRM and BI contracts are outstanding
 *      (TRD 11.4 open item 1, TRD 11.2), so an unconfigured endpoint is the expected state
 *      today. Reporting it as Unhealthy would make readiness red on every environment and
 *      train everyone to ignore it.
 *
 *   2. Any HTTP response means reachable. A 401 or a 404 proves the host is up and answering;
 *      whether the portal is authorised is a different question from whether the dependency
 *      is reachable, and TR-ARC-05 asks for the latter.
 *
 *   3. These checks never gate liveness. TR-NFR-07 requires the portal to stay usable in read
 *      mode when CRM or BI is unavailable, so a CRM outage must not cause IIS to recycle a
 *      portal that is still serving ISPs. /health/live consults none of them.
 */

/// <summary>Shared probe logic for the HTTP dependencies.</summary>
internal static class HttpDependencyProbe
{
    public static async Task<HealthCheckResult> ProbeAsync(
        IHttpClientFactory httpClientFactory,
        string clientName,
        Uri? baseAddress,
        string? healthPath,
        string dependencyName,
        string notConfiguredReason,
        CancellationToken cancellationToken)
    {
        if (baseAddress is null)
        {
            return HealthCheckResult.Degraded($"{dependencyName} is not configured. {notConfiguredReason}");
        }

        var client = httpClientFactory.CreateClient(clientName);
        var target = string.IsNullOrWhiteSpace(healthPath) ? baseAddress : new Uri(baseAddress, healthPath);

        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, target);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            var data = new Dictionary<string, object>
            {
                ["endpoint"] = target.GetLeftPart(UriPartial.Path),
                ["statusCode"] = (int)response.StatusCode,
                ["elapsedMs"] = Math.Round(elapsed.TotalMilliseconds, 1)
            };

            // Rule 2: answering at all is what reachability means here.
            return HealthCheckResult.Healthy($"{dependencyName} reachable.", data);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"{dependencyName} did not respond within the probe timeout.");
        }
        catch (HttpRequestException exception)
        {
            return HealthCheckResult.Unhealthy($"{dependencyName} unreachable.", exception);
        }
    }
}

/// <summary>CRM reachability (TRD 7.1 INT-CRM-01, -02, -04, -06, -08, -09).</summary>
public sealed class CrmHealthCheck : IHealthCheck
{
    /// <summary>Name this check is registered under.</summary>
    public const string Name = "crm";

    /// <summary>Named HttpClient used for probing, separate from the adapter's own client.</summary>
    public const string ClientName = "crm-health";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CrmOptions> _options;

    public CrmHealthCheck(IHttpClientFactory httpClientFactory, IOptions<CrmOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        HttpDependencyProbe.ProbeAsync(
            _httpClientFactory,
            ClientName,
            _options.Value.BaseAddress,
            _options.Value.HealthPath,
            "CRM",
            "The CRM contract is TRD 11.4 open item 1.",
            cancellationToken);
}

/// <summary>BI reachability (TRD 7.1 INT-BI-01, INT-BI-02).</summary>
public sealed class BiHealthCheck : IHealthCheck
{
    public const string Name = "bi";

    public const string ClientName = "bi-health";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<BiOptions> _options;

    public BiHealthCheck(IHttpClientFactory httpClientFactory, IOptions<BiOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        HttpDependencyProbe.ProbeAsync(
            _httpClientFactory,
            ClientName,
            _options.Value.BaseAddress,
            _options.Value.HealthPath,
            "BI",
            "The active-lines reference table structure is a TRD 11.2 dependency.",
            cancellationToken);
}

/// <summary>
/// SAP reachability (TRD 7.1 INT-SAP-01).
/// Reports Healthy while disabled: TR-INT-14 states that the absence of a financial code must
/// not block the activation or support flows, so a switched-off SAP adapter is a correct
/// state, not a degraded one.
/// </summary>
public sealed class SapHealthCheck : IHealthCheck
{
    public const string Name = "sap";

    public const string ClientName = "sap-health";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<SapOptions> _options;

    public SapHealthCheck(IHttpClientFactory httpClientFactory, IOptions<SapOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Value.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "SAP adapter disabled. The financial code population point is TRD 11.4 open item 5; " +
                "its absence does not block any flow (TR-INT-14).",
                new Dictionary<string, object> { ["enabled"] = false }));
        }

        return HttpDependencyProbe.ProbeAsync(
            _httpClientFactory,
            ClientName,
            _options.Value.BaseAddress,
            _options.Value.HealthPath,
            "SAP",
            "The financial code population point is TRD 11.4 open item 5.",
            cancellationToken);
    }
}

/// <summary>
/// SMTP relay reachability (TRD 7.1 INT-MAIL-01).
/// A TCP connect, not a message: TR-NTF-03 makes mail asynchronous precisely so that it never
/// blocks a business transaction, and a health probe that sends mail would be worse than the
/// problem it detects.
/// </summary>
public sealed class SmtpHealthCheck : IHealthCheck
{
    public const string Name = "smtp";

    private readonly IOptions<SmtpOptions> _options;

    public SmtpHealthCheck(IOptions<SmtpOptions> options) => _options = options;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return HealthCheckResult.Degraded("SMTP relay is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HealthCheckTimeout);

        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            return HealthCheckResult.Healthy(
                "SMTP relay reachable.",
                new Dictionary<string, object>
                {
                    ["host"] = options.Host,
                    ["port"] = options.Port,
                    ["elapsedMs"] = Math.Round(elapsed.TotalMilliseconds, 1),
                    // Worth surfacing: a production host in redirect mode is a misconfiguration
                    // that otherwise only shows up when someone asks why no mail arrived.
                    ["redirectAllMail"] = options.RedirectAllMail
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"SMTP relay {options.Host}:{options.Port} did not accept a connection within " +
                $"{options.HealthCheckTimeout.TotalSeconds:F0} s.");
        }
        catch (SocketException exception)
        {
            return HealthCheckResult.Unhealthy($"SMTP relay {options.Host}:{options.Port} unreachable.", exception);
        }
    }
}

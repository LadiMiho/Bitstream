using Bitstream.Application.Abstractions.Integration;

namespace Bitstream.Infrastructure.Integration.Bi;

/// <summary>Configuration of the BI adapter (TRD 6.1, TR-PAS-03).</summary>
public sealed class BiOptions
{
    public const string SectionName = "Integration:Bi";

    public Uri? BaseAddress { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Active-lines sync interval; default 60 minutes (TR-PAS-03).</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Technology codes presented to ISPs; configurable, not hard-coded (TR-PAS-02).</summary>
    public IList<string> IncludedTechnologies { get; set; } = ["GPON"];

    public int PageSize { get; set; } = 1000;

    /// <summary>Consecutive failed cycles before an alert is raised (TR-PAS-07).</summary>
    public int FailureAlertThreshold { get; set; } = 2;

    /// <summary>Path probed by the health check, relative to <see cref="BaseAddress"/>.</summary>
    public string? HealthPath { get; set; }

    /// <summary>Timeout for the health probe.</summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Adapter for the BI platform (TRD 7.1 INT-BI-01, INT-BI-02).
/// <para>
/// BLOCKED — TRD 11.2. The structure and access method of the active-lines reference table
/// have not been supplied by the BI team. The transport is expected to be either a REST
/// endpoint or a read-only view; both fit behind this port without touching the sync service.
/// </para>
/// </summary>
public sealed class BiGateway : IBiGateway
{
    private const string PendingContract =
        "BI active-lines reference table structure is not yet available (TRD 11.2).";

    /// <summary>Typed client supplied by <c>AddHttpClient</c>; used once the contract is available.</summary>
    private HttpClient Client { get; }

    public BiGateway(HttpClient httpClient) => Client = httpClient;

    public Task<IntegrationResult<ActiveLinesPage>> GetActiveLinesAsync(
        ActiveLinesQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    public Task<IntegrationResult<bool>> PublishReportingExtractAsync(
        ReportingExtractCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);
}

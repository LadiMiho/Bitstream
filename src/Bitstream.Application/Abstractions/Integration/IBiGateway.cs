namespace Bitstream.Application.Abstractions.Integration;

/// <summary>
/// Port for the BI platform (TRD 7.1 INT-BI-01, INT-BI-02).
/// The active-lines pull is scheduled and incremental (TR-PAS-03, TR-PAS-04).
/// </summary>
public interface IBiGateway
{
    /// <summary>INT-BI-01. Reads a page of the active-lines reference table.</summary>
    Task<IntegrationResult<ActiveLinesPage>> GetActiveLinesAsync(
        ActiveLinesQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>INT-BI-02. Publishes the portal reporting extract to BI (TR-REP-10).</summary>
    Task<IntegrationResult<bool>> PublishReportingExtractAsync(
        ReportingExtractCommand command,
        CancellationToken cancellationToken = default);
}

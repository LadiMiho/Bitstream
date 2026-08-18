using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>Thrown when a synchronisation run cannot complete. The presentation layer maps this to a problem response.</summary>
public sealed class ActiveLineSyncException : Exception
{
    public ActiveLineSyncException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Implements <see cref="IActiveLineSyncService"/>: TRD 6.1, the BI active-lines pull.
/// <para>
/// Incremental and idempotent (TR-PAS-04): the stored <see cref="Domain.Entities.SyncState.ChangeMarker"/>
/// is the cursor for the next run, and the upsert key is (IspId, ContractId) — re-running the
/// same page twice updates the same rows rather than duplicating them.
/// </para>
/// </summary>
public sealed class ActiveLineSyncService : IActiveLineSyncService
{
    /// <summary>Row key in <c>ops.SyncState</c> for this job — also used by the Api layer's operations endpoint to read status directly from <see cref="ISyncStateStore"/>.</summary>
    public const string SyncKey = "BiActiveLines";

    private readonly IBiGateway _biGateway;
    private readonly IActiveLineRepository _lineRepository;
    private readonly IIspRepository _ispRepository;
    private readonly ISyncStateStore _syncStateStore;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<ActiveLineSyncOptions> _pageSizeOptions;
    private readonly ILogger<ActiveLineSyncService> _logger;

    public ActiveLineSyncService(
        IBiGateway biGateway,
        IActiveLineRepository lineRepository,
        IIspRepository ispRepository,
        ISyncStateStore syncStateStore,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        IOptionsMonitor<ActiveLineSyncOptions> pageSizeOptions,
        ILogger<ActiveLineSyncService> logger)
    {
        _biGateway = biGateway;
        _lineRepository = lineRepository;
        _ispRepository = ispRepository;
        _syncStateStore = syncStateStore;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _pageSizeOptions = pageSizeOptions;
        _logger = logger;
    }

    public async Task<int> SynchroniseAsync(bool fullReload, CancellationToken cancellationToken = default)
    {
        var syncState = await _syncStateStore.GetOrCreateAsync(SyncKey, cancellationToken).ConfigureAwait(false);
        var changeMarker = fullReload ? null : syncState.ChangeMarker;
        var pageSize = _pageSizeOptions.CurrentValue.PageSize;

        var touched = 0;
        var pageNumber = 1;
        string? nextChangeMarker = changeMarker;

        while (true)
        {
            var result = await _biGateway.GetActiveLinesAsync(
                new ActiveLinesQuery(changeMarker, pageSize, pageNumber), cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                syncState.LastRunAt = _clock.UtcNow;
                syncState.ConsecutiveFailures++;
                await _syncStateStore.SaveAsync(syncState, cancellationToken).ConfigureAwait(false);

                throw new ActiveLineSyncException(result.ErrorMessage ?? result.Outcome.ToString());
            }

            var page = result.Value!;

            foreach (var record in page.Lines)
            {
                touched += await UpsertAsync(record, cancellationToken).ConfigureAwait(false) ? 1 : 0;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            nextChangeMarker = page.NextChangeMarker;

            if (!page.HasMore)
            {
                break;
            }

            pageNumber++;
        }

        var now = _clock.UtcNow;
        syncState.LastRunAt = now;
        syncState.LastSuccessfulSyncAt = now;
        syncState.ConsecutiveFailures = 0;
        syncState.ChangeMarker = nextChangeMarker;
        await _syncStateStore.SaveAsync(syncState, cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ActiveLineSync.Completed", "SyncState", SyncKey, null, $"{{\"touched\":{touched},\"fullReload\":{(fullReload ? "true" : "false")}}}",
            cancellationToken).ConfigureAwait(false);

        return touched;
    }

    public async Task<DateTimeOffset?> GetLastSuccessfulSyncAsync(CancellationToken cancellationToken = default)
    {
        var state = await _syncStateStore.GetOrCreateAsync(SyncKey, cancellationToken).ConfigureAwait(false);

        return state.LastSuccessfulSyncAt;
    }

    private async Task<bool> UpsertAsync(ActiveLineRecord record, CancellationToken cancellationToken)
    {
        var isp = await _ispRepository.FindByCrmBpReferenceAsync(record.IspCrmBpReference, cancellationToken).ConfigureAwait(false);

        if (isp is null)
        {
            _logger.LogWarning("Active-lines sync: no ISP found for Business Partner '{BusinessPartner}'; row skipped.", record.IspCrmBpReference);
            return false;
        }

        var line = await _lineRepository.FindByIspAndContractAsync(isp.IspId, record.ContractId, cancellationToken).ConfigureAwait(false);

        if (line is null)
        {
            line = new ActiveLine
            {
                IspId = isp.IspId,
                ContractId = record.ContractId,
                SubscriberReference = record.SubscriberReference,
                Technology = record.Technology,
                PackageCode = record.PackageCode,
                Status = record.Status,
                BiSyncedAt = _clock.UtcNow,
                BiChangeMarker = record.ChangeMarker
            };
            await _lineRepository.AddAsync(line, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            line.SubscriberReference = record.SubscriberReference;
            line.Technology = record.Technology;
            line.PackageCode = record.PackageCode;
            line.Status = record.Status;
            line.BiSyncedAt = _clock.UtcNow;
            line.BiChangeMarker = record.ChangeMarker;
        }

        return true;
    }
}

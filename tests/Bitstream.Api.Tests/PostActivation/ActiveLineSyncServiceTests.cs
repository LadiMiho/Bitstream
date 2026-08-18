using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bitstream.Api.Tests.PostActivation;

/// <summary>TRD 6.1: the BI active-lines pull is incremental and idempotent (TR-PAS-04).</summary>
public sealed class ActiveLineSyncServiceTests
{
    private sealed class FakeBiGateway : IBiGateway
    {
        public List<ActiveLineRecord> Records { get; set; } = [];

        public int Calls { get; private set; }

        public IntegrationResult<ActiveLinesPage>? NextResult { get; set; }

        public Task<IntegrationResult<ActiveLinesPage>> GetActiveLinesAsync(ActiveLinesQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;

            if (NextResult is { } forced)
            {
                return Task.FromResult(forced);
            }

            return Task.FromResult(IntegrationResult<ActiveLinesPage>.Success(new ActiveLinesPage(Records, "marker-1", false)));
        }

        public Task<IntegrationResult<bool>> PublishReportingExtractAsync(ReportingExtractCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeActiveLineRepository : IActiveLineRepository
    {
        public Dictionary<(long IspId, string ContractId), ActiveLine> Lines { get; } = [];

        private long _nextId = 1;

        public Task<ActiveLine?> FindByIdAsync(long lineId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lines.Values.FirstOrDefault(l => l.LineId == lineId));

        public Task<ActiveLine?> FindByIspAndContractAsync(long ispId, string contractId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lines.GetValueOrDefault((ispId, contractId)));

        public Task AddAsync(ActiveLine line, CancellationToken cancellationToken = default)
        {
            line.LineId = _nextId++;
            Lines[(line.IspId, line.ContractId)] = line;
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Lines.Count);
    }

    private sealed class FakeSyncStateStore : ISyncStateStore
    {
        public SyncState State { get; } = new() { SyncKey = "BiActiveLines" };

        public Task<SyncState> GetOrCreateAsync(string syncKey, CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task SaveAsync(SyncState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private readonly FakeBiGateway _biGateway = new();
    private readonly FakeActiveLineRepository _lineRepository = new();
    private readonly FakeIspRepository _ispRepository = new();
    private readonly FakeSyncStateStore _syncStateStore = new();
    private readonly FakeAuditWriter _auditWriter = new();
    private readonly FakeClock _clock = new();

    private ActiveLineSyncService CreateService() =>
        new(
            _biGateway,
            _lineRepository,
            _ispRepository,
            _syncStateStore,
            new FakeUnitOfWork(),
            _auditWriter,
            _clock,
            new TestOptionsMonitor<ActiveLineSyncOptions>(new ActiveLineSyncOptions()),
            NullLogger<ActiveLineSyncService>.Instance);

    private Isp AddIsp(long ispId, string bp)
    {
        var isp = new Isp
        {
            IspId = ispId, Name = "Alpha", Nipt = "L1", ContactPerson = "A",
            ContactEmail = "a@example.com", ContactMobile = "+355691234567", CrmBpReference = bp
        };
        _ispRepository.Isps[ispId] = isp;
        return isp;
    }

    [Fact]
    public async Task SynchroniseAsync_creates_a_new_line_and_records_success()
    {
        AddIsp(1, "BP-1");
        _biGateway.Records = [new ActiveLineRecord("BP-1", "CTR-1", "SUB-1", "GPON", "BITSTREAM_STD", "Active", "marker-1")];
        var service = CreateService();

        var touched = await service.SynchroniseAsync(fullReload: false);

        Assert.Equal(1, touched);
        Assert.Single(_lineRepository.Lines);
        Assert.NotNull(await service.GetLastSuccessfulSyncAsync());
        Assert.Equal(0, _syncStateStore.State.ConsecutiveFailures);
    }

    [Fact]
    public async Task SynchroniseAsync_upserts_rather_than_duplicates_on_a_repeated_run()
    {
        // TR-PAS-04: the same contract synced twice must update the one row, not create a second.
        AddIsp(1, "BP-1");
        _biGateway.Records = [new ActiveLineRecord("BP-1", "CTR-1", "SUB-1", "GPON", "BITSTREAM_STD", "Active", "marker-1")];
        var service = CreateService();

        await service.SynchroniseAsync(fullReload: false);

        _biGateway.Records = [new ActiveLineRecord("BP-1", "CTR-1", "SUB-1-UPDATED", "GPON", "BITSTREAM_PRO", "Active", "marker-2")];
        await service.SynchroniseAsync(fullReload: false);

        Assert.Single(_lineRepository.Lines);
        var line = _lineRepository.Lines[(1, "CTR-1")];
        Assert.Equal("SUB-1-UPDATED", line.SubscriberReference);
        Assert.Equal("BITSTREAM_PRO", line.PackageCode);
    }

    [Fact]
    public async Task SynchroniseAsync_skips_a_record_for_an_unknown_Business_Partner()
    {
        // No ISP with this BP has been seeded.
        _biGateway.Records = [new ActiveLineRecord("BP-UNKNOWN", "CTR-9", "SUB-9", "GPON", "BITSTREAM_STD", "Active", null)];
        var service = CreateService();

        var touched = await service.SynchroniseAsync(fullReload: false);

        Assert.Equal(0, touched);
        Assert.Empty(_lineRepository.Lines);
    }

    [Fact]
    public async Task SynchroniseAsync_records_a_failure_without_touching_LastSuccessfulSyncAt()
    {
        _biGateway.NextResult = IntegrationResult<ActiveLinesPage>.TechnicalFailure("BI unreachable");
        var service = CreateService();

        await Assert.ThrowsAsync<ActiveLineSyncException>(() => service.SynchroniseAsync(fullReload: false));

        Assert.Equal(1, _syncStateStore.State.ConsecutiveFailures);
        Assert.Null(_syncStateStore.State.LastSuccessfulSyncAt);
    }
}

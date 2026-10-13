using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Services.Dashboard;

public interface IDashboardService
{
    /// <summary>
    /// The signed-in user's dashboard. A user with <c>activation.read.all</c> (Administrator,
    /// Auditor, Service Desk) sees every ISP; anyone else sees only their own ISP — the same
    /// ownership rule as the activation request screens (TR-SEC-18). A user with neither gets an
    /// empty dashboard rather than another ISP's figures.
    /// </summary>
    Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class DashboardService : IDashboardService
{
    private const int Months = 6;
    private const int Recent = 6;

    private static readonly ActivationRequestStatus[] FinalStatuses =
    [
        ActivationRequestStatus.Completed,
        ActivationRequestStatus.Closed,
        ActivationRequestStatus.RejectedNoLine,
        ActivationRequestStatus.ActivationFailed,
        ActivationRequestStatus.IntegrationFailed
    ];

    private readonly IDashboardQueries _queries;
    private readonly IIspRepository _ispRepository;
    private readonly ICurrentUserContext _currentUser;
    private readonly IClock _clock;

    public DashboardService(IDashboardQueries queries, IIspRepository ispRepository, ICurrentUserContext currentUser, IClock clock)
    {
        _queries = queries;
        _ispRepository = ispRepository;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default)
    {
        var allIsps = _currentUser.HasPermission(ActivationPermissionCodes.ActivationReadAll);
        long? ispId = allIsps ? null : _currentUser.IspId;

        var today = _clock.UtcNow.UtcDateTime.Date;
        var monthsFrom = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(Months - 1));

        if (!allIsps && ispId is null)
        {
            return Summarise(null, false, Empty(monthsFrom));
        }

        var data = await _queries.GetAsync(ispId, monthsFrom, Months, Recent, cancellationToken).ConfigureAwait(false);
        var ispName = ispId is { } id ? (await _ispRepository.FindByIdAsync(id, cancellationToken).ConfigureAwait(false))?.Name : null;

        return Summarise(ispName, allIsps, data);
    }

    private static DashboardSummary Summarise(string? ispName, bool allIsps, DashboardData data)
    {
        int Count(params ActivationRequestStatus[] statuses) => statuses.Sum(s => data.RequestsByStatus.GetValueOrDefault(s));

        var total = data.RequestsByStatus.Values.Sum();
        var final = Count(FinalStatuses);

        return new DashboardSummary(
            ispName,
            allIsps,
            total,
            InProgress: total - final,
            AwaitingOperator: Count(ActivationRequestStatus.AwaitingOperatorConfirmation),
            WaitingForServiceDesk: Count(ActivationRequestStatus.WaitingForServiceDesk),
            Completed: Count(ActivationRequestStatus.Completed),
            Failed: Count(ActivationRequestStatus.RejectedNoLine, ActivationRequestStatus.ActivationFailed, ActivationRequestStatus.IntegrationFailed),
            AverageDaysToComplete: data.CompletionDays.Count == 0 ? null : Math.Round(data.CompletionDays.Average(), 1),
            data.RequestsByStatus,
            data.SubmittedPerMonth,
            data.RecentRequests,
            data.OpenTickets,
            data.ClosedTickets,
            data.ActiveLines,
            data.TopIsps,
            data.OutboundByStatus);
    }

    private static DashboardData Empty(DateTime monthsFrom) => new(
        new Dictionary<ActivationRequestStatus, int>(),
        [.. Enumerable.Range(0, Months).Select(i => (monthsFrom.AddMonths(i), 0))],
        [],
        [],
        0,
        0,
        0,
        [],
        new Dictionary<IntegrationMessageStatus, int>());
}

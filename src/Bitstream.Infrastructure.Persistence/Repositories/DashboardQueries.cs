using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services.Dashboard;
using Bitstream.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IDashboardQueries"/>: grouped counts, read-only and untracked.</summary>
public sealed class DashboardQueries : IDashboardQueries
{
    private readonly BitstreamDbContext _dbContext;

    public DashboardQueries(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<DashboardData> GetAsync(long? ispId, DateTime monthsFrom, int months, int recent, CancellationToken cancellationToken = default)
    {
        var requests = _dbContext.ActivationRequests.AsNoTracking();
        var tickets = _dbContext.ComplaintTickets.AsNoTracking();
        var lines = _dbContext.ActiveLines.AsNoTracking();

        if (ispId is { } id)
        {
            requests = requests.Where(r => r.IspId == id);
            tickets = tickets.Where(t => t.IspId == id);
            lines = lines.Where(l => l.IspId == id);
        }

        var byStatus = await requests
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        var from = new DateTimeOffset(monthsFrom, TimeSpan.Zero);
        var submitted = await requests
            .Where(r => r.CreatedAt >= from)
            .Select(r => r.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var perMonth = Enumerable.Range(0, months)
            .Select(i => monthsFrom.AddMonths(i))
            .Select(month => (month, submitted.Count(at => at.UtcDateTime.Year == month.Year && at.UtcDateTime.Month == month.Month)))
            .ToList();

        var completed = await requests
            .Where(r => r.Status == ActivationRequestStatus.Completed && r.LastUpdatedAt != null && r.LastUpdatedAt >= from)
            .Select(r => new { r.CreatedAt, r.LastUpdatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recentRequests = await requests
            .OrderByDescending(r => r.CreatedAt)
            .Take(recent)
            .Select(r => new DashboardRecentRequest(r.PublicId, r.Isp.Name, r.PackageCode, r.Status, r.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var openTickets = await tickets.CountAsync(t => t.ClosedAt == null, cancellationToken).ConfigureAwait(false);
        var closedTickets = await tickets.CountAsync(t => t.ClosedAt != null, cancellationToken).ConfigureAwait(false);
        var activeLines = await lines.CountAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<(string, int)> topIsps = [];
        IReadOnlyDictionary<IntegrationMessageStatus, int> outbound = new Dictionary<IntegrationMessageStatus, int>();

        // Every-ISP view only: comparisons between ISPs and the CRM integration's health are not
        // an ISP's business.
        if (ispId is null)
        {
            var top = await requests
                .GroupBy(r => r.Isp.Name)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            topIsps = [.. top.Select(x => (x.Name, x.Count))];

            outbound = await _dbContext.IntegrationMessages.AsNoTracking()
                .Where(m => m.Direction == IntegrationDirection.Outbound)
                .GroupBy(m => m.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken)
                .ConfigureAwait(false);
        }

        return new DashboardData(
            byStatus,
            perMonth,
            [.. completed.Select(c => (c.LastUpdatedAt!.Value - c.CreatedAt).TotalDays)],
            recentRequests,
            openTickets,
            closedTickets,
            activeLines,
            topIsps,
            outbound);
    }
}

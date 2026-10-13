using Bitstream.Domain.Enums;

namespace Bitstream.Application.Services.Dashboard;

/// <summary>Raw counts behind the dashboard, for one ISP or for every ISP.</summary>
/// <param name="RequestsByStatus">Activation requests per status.</param>
/// <param name="SubmittedPerMonth">Requests submitted per calendar month (first of the month), oldest first, for the months asked.</param>
/// <param name="CompletionDays">Days from submission to completion, for requests completed in the period asked.</param>
/// <param name="RecentRequests">The latest requests, newest first.</param>
/// <param name="OpenTickets">Complaint tickets not yet closed.</param>
/// <param name="ClosedTickets">Complaint tickets closed.</param>
/// <param name="ActiveLines">Active lines (BI sync).</param>
/// <param name="TopIsps">Most activation requests per ISP; empty when scoped to one ISP.</param>
/// <param name="OutboundByStatus">Messages to CRM per status; empty when scoped to one ISP.</param>
public sealed record DashboardData(
    IReadOnlyDictionary<ActivationRequestStatus, int> RequestsByStatus,
    IReadOnlyList<(DateTime Month, int Count)> SubmittedPerMonth,
    IReadOnlyList<double> CompletionDays,
    IReadOnlyList<DashboardRecentRequest> RecentRequests,
    int OpenTickets,
    int ClosedTickets,
    int ActiveLines,
    IReadOnlyList<(string IspName, int Count)> TopIsps,
    IReadOnlyDictionary<IntegrationMessageStatus, int> OutboundByStatus);

public sealed record DashboardRecentRequest(string PublicId, string IspName, string PackageCode, ActivationRequestStatus Status, DateTimeOffset CreatedAt);

/// <summary>What the dashboard shows. <see cref="IspName"/> is set when it is scoped to one ISP.</summary>
public sealed record DashboardSummary(
    string? IspName,
    bool IsAllIsps,
    int TotalRequests,
    int InProgress,
    int AwaitingOperator,
    int WaitingForServiceDesk,
    int Completed,
    int Failed,
    double? AverageDaysToComplete,
    IReadOnlyDictionary<ActivationRequestStatus, int> RequestsByStatus,
    IReadOnlyList<(DateTime Month, int Count)> SubmittedPerMonth,
    IReadOnlyList<DashboardRecentRequest> RecentRequests,
    int OpenTickets,
    int ClosedTickets,
    int ActiveLines,
    IReadOnlyList<(string IspName, int Count)> TopIsps,
    IReadOnlyDictionary<IntegrationMessageStatus, int> OutboundByStatus);

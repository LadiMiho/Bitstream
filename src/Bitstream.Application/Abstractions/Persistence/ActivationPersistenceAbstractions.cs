using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>Data access for the activation request lifecycle, TRD 5.</summary>
public interface IActivationRequestRepository
{
    Task<ActivationRequest?> FindByIdAsync(long requestId, CancellationToken cancellationToken = default);

    /// <summary>Looked up by the public identifier — the value every endpoint outside this
    /// module and every integration message addresses the request by (TR-DAT-04).</summary>
    Task<ActivationRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    /// <summary>Looked up by CRM's ticket number (EV_TICKET_NO), the identifier CRM itself holds for the request.</summary>
    Task<ActivationRequest?> FindByCrmTicketIdAsync(string crmTicketId, CancellationToken cancellationToken = default);

    Task AddAsync(ActivationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The grid's search and filters (<see cref="ActivationRequestSearchFilter"/>), most recently
    /// created first, with <see cref="ActivationRequest.Isp"/> loaded for display. The ISP filter
    /// is also the ownership scope: <c>ActivationRequestService.SearchAsync</c> forces it to the
    /// caller's own ISP for anyone without <c>activation.read.all</c>.
    /// </summary>
    Task<(IReadOnlyList<ActivationRequest> Items, int TotalCount)> SearchAsync(
        ActivationRequestSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read access to the activation request form's reference catalogues — packages (TR-ACT-01),
/// ticket classifications (TR-ACT-04) and contract durations (TRD 5.1). DB-backed so they can be
/// maintained without a release, replacing the previous <c>Catalogues</c> configuration lists.
/// Each method returns every row, active and inactive alike — a package or classification that
/// has since been retired must still resolve (e.g. TR-PAS-35's upgrade/downgrade eligibility
/// needs a line's *current* package even once it is no longer offered); callers decide what
/// "offered right now" means from <c>IsActive</c>.
/// </summary>
public interface IActivationCatalogueRepository
{
    Task<IReadOnlyList<Package>> GetPackagesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivationClassification>> GetClassificationsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContractDuration>> GetContractDurationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Package + contract duration combinations and their CRM codes (portal.PackageOffer).</summary>
    Task<IReadOnlyList<PackageOffer>> GetPackageOffersAsync(CancellationToken cancellationToken = default);

    /// <summary>The offer for one package + duration pair, tracked for update; null when there is none.</summary>
    Task<PackageOffer?> FindPackageOfferAsync(string packageCode, int contractDurationMonths, CancellationToken cancellationToken = default);

    /// <summary>The offer using <paramref name="offerCode"/>, whichever pair it belongs to; null when unused.</summary>
    Task<PackageOffer?> FindPackageOfferByCodeAsync(string offerCode, CancellationToken cancellationToken = default);

    /// <summary>Stages a new offer; persisted by <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    Task AddPackageOfferAsync(PackageOffer offer, CancellationToken cancellationToken = default);

    /// <summary>The package with <paramref name="code"/>, tracked for update; null when there is none.</summary>
    Task<Package?> FindPackageAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Stages a new package; persisted by <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    Task AddPackageAsync(Package package, CancellationToken cancellationToken = default);

    /// <summary>The contract duration of <paramref name="months"/>, tracked for update; null when there is none.</summary>
    Task<ContractDuration?> FindContractDurationAsync(int months, CancellationToken cancellationToken = default);

    /// <summary>Stages a new contract duration; persisted by <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    Task AddContractDurationAsync(ContractDuration duration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything stored about one activation request's history, for its timeline: the integration
/// messages exchanged with CRM (both directions) and the portal users' audited actions on it.
/// </summary>
/// <param name="Messages">Outbound calls and inbound CRM events related to the request, by portal ID or CRM ticket number.</param>
/// <param name="AuditEntries">Audit rows for the request that a signed-in user caused (system and CRM-driven rows excluded).</param>
/// <param name="ActorNames">Full name of each user in <paramref name="AuditEntries"/>, by user ID.</param>
public sealed record ActivationHistory(
    IReadOnlyList<IntegrationMessage> Messages,
    IReadOnlyList<AuditLog> AuditEntries,
    IReadOnlyDictionary<long, string> ActorNames);

/// <summary>Reads <see cref="ActivationHistory"/> for the View drawer's timeline.</summary>
public interface IActivationHistoryReader
{
    Task<ActivationHistory> GetAsync(ActivationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Read-only aggregates for the dashboard.</summary>
public interface IDashboardQueries
{
    /// <param name="ispId">One ISP's figures, or null for every ISP.</param>
    /// <param name="monthsFrom">First day of the oldest month in <see cref="Services.Dashboard.DashboardData.SubmittedPerMonth"/>.</param>
    /// <param name="months">How many months to return, ending with the current one.</param>
    /// <param name="recent">How many recent requests to return.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Services.Dashboard.DashboardData> GetAsync(long? ispId, DateTime monthsFrom, int months, int recent, CancellationToken cancellationToken = default);
}

/// <summary>The Activation Requests grid's search box and Filters popup; every criterion is optional and they combine with AND.</summary>
/// <param name="Search">Case-insensitive substring of the public ID or package code.</param>
/// <param name="Status">Exact status.</param>
/// <param name="IspId">One ISP's requests.</param>
/// <param name="PackageCode">Exact package code.</param>
/// <param name="ContractDurationMonths">Exact contract duration.</param>
/// <param name="SubmittedFrom">Submitted at or after this instant.</param>
/// <param name="SubmittedBefore">Submitted before this instant (the day after the "to" date, so that day is included).</param>
public sealed record ActivationRequestSearchFilter(
    string? Search = null,
    ActivationRequestStatus? Status = null,
    long? IspId = null,
    string? PackageCode = null,
    int? ContractDurationMonths = null,
    DateTimeOffset? SubmittedFrom = null,
    DateTimeOffset? SubmittedBefore = null);

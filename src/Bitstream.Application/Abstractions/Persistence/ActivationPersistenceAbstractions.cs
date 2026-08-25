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

    Task AddAsync(ActivationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Case-insensitive substring match against the public ID and package code, most recently
    /// created first, with <see cref="ActivationRequest.Isp"/> loaded for display.
    /// <paramref name="ispId"/> restricts to one ISP's requests — the ownership scoping
    /// <c>ActivationRequestService.SearchAsync</c> applies before calling this, not a filter the
    /// caller opts into. <paramref name="status"/> narrows the grid further when given.
    /// </summary>
    Task<(IReadOnlyList<ActivationRequest> Items, int TotalCount)> SearchAsync(
        string? search, ActivationRequestStatus? status, long? ispId, int skip, int take, CancellationToken cancellationToken = default);
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
}

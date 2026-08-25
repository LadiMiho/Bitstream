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

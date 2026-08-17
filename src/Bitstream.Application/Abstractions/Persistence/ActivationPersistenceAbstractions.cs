using Bitstream.Domain.Entities;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>Data access for the activation request lifecycle, TRD 5.</summary>
public interface IActivationRequestRepository
{
    Task<ActivationRequest?> FindByIdAsync(long requestId, CancellationToken cancellationToken = default);

    /// <summary>Looked up by the public identifier — the value every endpoint outside this
    /// module and every integration message addresses the request by (TR-DAT-04).</summary>
    Task<ActivationRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    Task AddAsync(ActivationRequest request, CancellationToken cancellationToken = default);
}

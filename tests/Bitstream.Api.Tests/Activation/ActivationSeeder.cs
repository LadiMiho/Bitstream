using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;

namespace Bitstream.Api.Tests.Activation;

/// <summary>
/// Seeds <see cref="BitstreamDbContext"/> directly for the HTTP-level tests, bypassing
/// <c>IActivationRequestService</c> — the same reasoning as
/// <c>tests/Bitstream.Api.Tests/Identity/IdentitySeeder.cs</c>: submission itself
/// (<c>SqlPublicIdentifierGenerator</c>) needs a real SQL Server connection for its stored
/// procedure call, which is not available under the InMemory provider these tests run against,
/// so it is exercised by <c>ActivationRequestServiceTests</c> against fakes instead. What is
/// tested here is what happens once a request already exists at a given status: ownership
/// scoping and the GIS verification branches.
/// </summary>
internal static class ActivationSeeder
{
    public static async Task<ActivationRequest> AddRequestAsync(
        BitstreamDbContext db,
        long ispId,
        string publicId,
        ActivationRequestStatus status = ActivationRequestStatus.Submitted)
    {
        var request = new ActivationRequest
        {
            PublicId = publicId,
            IspId = ispId,
            PackageCode = "BITSTREAM_STD",
            LocationRaw = "41.3275,19.8187",
            LocationLat = 41.3275m,
            LocationLng = 19.8187m,
            Classification = "REQUEST_FOR_ACTIVATION",
            ContractDurationMonths = 12,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.ActivationRequests.Add(request);
        await db.SaveChangesAsync();

        return request;
    }
}

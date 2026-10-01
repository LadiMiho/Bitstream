using System.Text.Json.Serialization;

namespace Bitstream.Web.Contracts;

public sealed record CreatePackageOfferHttpRequest(
    [property: JsonPropertyName("packageCode")] string PackageCode,
    [property: JsonPropertyName("contractDurationMonths")] int ContractDurationMonths,
    [property: JsonPropertyName("offerCode")] string OfferCode);

public sealed record UpdatePackageOfferHttpRequest([property: JsonPropertyName("offerCode")] string OfferCode);

public sealed record SetPackageOfferActiveHttpRequest([property: JsonPropertyName("isActive")] bool IsActive);

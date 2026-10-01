using System.Text.Json.Serialization;

namespace Bitstream.Web.Contracts;

public sealed record CreatePackageHttpRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tier")] int Tier);

public sealed record UpdatePackageHttpRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tier")] int Tier);

public sealed record CreateContractDurationHttpRequest(
    [property: JsonPropertyName("months")] int Months,
    [property: JsonPropertyName("label")] string? Label);

public sealed record UpdateContractDurationHttpRequest([property: JsonPropertyName("label")] string? Label);

/// <summary>Body of every activate/deactivate action on the catalogue screens.</summary>
public sealed record SetActiveHttpRequest([property: JsonPropertyName("isActive")] bool IsActive);

using System.Text.Json.Serialization;

namespace Bitstream.Web.Contracts;

/// <param name="IspId">Owning ISP. Must equal the caller's own ISP for an ISP user; an internal caller may submit on behalf of any ISP.</param>
/// <param name="PackageCode">From the configured catalogue (TR-ACT-01).</param>
/// <param name="LocationRaw">A map URL or a 'latitude,longitude' pair, exactly as entered (TR-ACT-02).</param>
/// <param name="Classification">Defaults to the configured default classification when omitted (TR-ACT-04).</param>
/// <param name="ContractDurationMonths">One of the configured selectable durations, e.g. 12 or 24.</param>
/// <param name="Comments">Free text, max 2000 characters, HTML stripped (TR-ACT-05).</param>
public sealed record SubmitActivationHttpRequest(
    [property: JsonPropertyName("ispId")] long IspId,
    [property: JsonPropertyName("packageCode")] string PackageCode,
    [property: JsonPropertyName("locationRaw")] string LocationRaw,
    [property: JsonPropertyName("classification")] string? Classification,
    [property: JsonPropertyName("contractDurationMonths")] int ContractDurationMonths,
    [property: JsonPropertyName("comments")] string? Comments);

public sealed record ActivationRequestResponse(
    [property: JsonPropertyName("requestId")] long RequestId,
    [property: JsonPropertyName("publicId")] string PublicId,
    [property: JsonPropertyName("ispId")] long IspId,
    [property: JsonPropertyName("packageCode")] string PackageCode,
    [property: JsonPropertyName("locationRaw")] string LocationRaw,
    [property: JsonPropertyName("locationLat")] decimal LocationLat,
    [property: JsonPropertyName("locationLng")] decimal LocationLng,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("contractDurationMonths")] int ContractDurationMonths,
    [property: JsonPropertyName("comments")] string? Comments,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("statusReason")] string? StatusReason,
    [property: JsonPropertyName("salesOrderId")] string? SalesOrderId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastUpdatedAt")] DateTimeOffset? LastUpdatedAt);

/// <summary>
/// Body of the GIS verification admin screen's outcome submission (TR-ACT-12 to TR-ACT-19).
/// </summary>
/// <param name="LineAvailable">True for the line-exists branch, false for the no-line branch.</param>
/// <param name="Reason">Required when <see cref="LineAvailable"/> is false (TR-ACT-13); ignored otherwise.</param>
public sealed record GisOutcomeRequest(
    [property: JsonPropertyName("lineAvailable")] bool LineAvailable,
    [property: JsonPropertyName("reason")] string? Reason);

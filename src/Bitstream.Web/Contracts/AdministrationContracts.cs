using System.Text.Json.Serialization;

namespace Bitstream.Web.Contracts;

public sealed record CreateIspHttpRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nipt")] string Nipt,
    [property: JsonPropertyName("contactPerson")] string ContactPerson,
    [property: JsonPropertyName("contactEmail")] string ContactEmail,
    [property: JsonPropertyName("contactMobile")] string ContactMobile,
    [property: JsonPropertyName("crmBpReference")] string CrmBpReference);

public sealed record IspResponse(
    [property: JsonPropertyName("ispId")] long IspId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nipt")] string Nipt,
    [property: JsonPropertyName("contactPerson")] string ContactPerson,
    [property: JsonPropertyName("contactEmail")] string ContactEmail,
    [property: JsonPropertyName("contactMobile")] string ContactMobile,
    [property: JsonPropertyName("crmBpReference")] string CrmBpReference,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <param name="Status"><c>Active</c> or <c>Locked</c> (TR-SEC-11).</param>
public sealed record SetStatusRequest([property: JsonPropertyName("status")] string Status);

/// <param name="IspId">Owning ISP, or null for an internal user (TR-SEC-14).</param>
/// <param name="FullName">User's full name.</param>
/// <param name="Email">RFC-compliant, unique across the portal.</param>
/// <param name="Mobile">E.164 format.</param>
/// <param name="RoleName">Administrator, IspUser, ServiceDesk or Auditor.</param>
/// <param name="InitialPassword">Must satisfy the configured password policy (TR-SEC-03).</param>
public sealed record CreateUserHttpRequest(
    [property: JsonPropertyName("ispId")] long? IspId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("mobile")] string Mobile,
    [property: JsonPropertyName("roleName")] string RoleName,
    [property: JsonPropertyName("initialPassword")] string InitialPassword);

public sealed record UserResponse(
    [property: JsonPropertyName("userId")] long UserId,
    [property: JsonPropertyName("ispId")] long? IspId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("mobile")] string Mobile,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastLoginAt")] DateTimeOffset? LastLoginAt);

/// <summary>Body of a 400/422 validation failure (TR-NFR-12: specific, field-level, actionable).</summary>
public sealed record ValidationProblemBody([property: JsonPropertyName("violations")] IReadOnlyList<string> Violations);

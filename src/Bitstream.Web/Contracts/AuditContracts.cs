using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Contracts;

/// <summary>The Audit Logs grid's search box and Filters popup, shared by Search and Export.</summary>
public sealed record AuditLogSearchQuery
{
    [FromQuery(Name = "search")]
    public string? Search { get; init; }

    [FromQuery(Name = "actionCode")]
    public string? ActionCode { get; init; }

    [FromQuery(Name = "entityType")]
    public string? EntityType { get; init; }

    /// <summary>A user ID, or <c>system</c> for actions without a user.</summary>
    [FromQuery(Name = "actor")]
    public string? Actor { get; init; }

    /// <summary>At or after: the browser's local midnight of the "from" date.</summary>
    [FromQuery(Name = "from")]
    public DateTimeOffset? From { get; init; }

    /// <summary>Before: the browser's local midnight after the "to" date.</summary>
    [FromQuery(Name = "before")]
    public DateTimeOffset? Before { get; init; }
}

public sealed record AuditLogSummaryResponse(
    [property: JsonPropertyName("auditId")] long AuditId,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("actorName")] string ActorName,
    [property: JsonPropertyName("actorIp")] string? ActorIp,
    [property: JsonPropertyName("actionCode")] string ActionCode,
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityId")] string? EntityId,
    [property: JsonPropertyName("isSystem")] bool IsSystem);

/// <param name="Items">At most <c>take</c> rows.</param>
/// <param name="TotalCount">Every matching row, ignoring paging.</param>
public sealed record AuditLogListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<AuditLogSummaryResponse> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount);

using System.Text.Json.Serialization;

namespace Bitstream.Api.Contracts;

public sealed record CreateComplaintTicketHttpRequest(
    [property: JsonPropertyName("ispId")] long IspId,
    [property: JsonPropertyName("lineId")] long LineId,
    [property: JsonPropertyName("categoryL1")] string CategoryL1,
    [property: JsonPropertyName("categoryL2")] string CategoryL2,
    [property: JsonPropertyName("categoryL3")] string CategoryL3,
    [property: JsonPropertyName("description")] string Description);

public sealed record ComplaintTicketResponse(
    [property: JsonPropertyName("ticketId")] long TicketId,
    [property: JsonPropertyName("publicId")] string PublicId,
    [property: JsonPropertyName("ispId")] long IspId,
    [property: JsonPropertyName("lineId")] long LineId,
    [property: JsonPropertyName("categoryL1")] string CategoryL1,
    [property: JsonPropertyName("categoryL2")] string CategoryL2,
    [property: JsonPropertyName("categoryL3")] string CategoryL3,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("crmTicketId")] string? CrmTicketId,
    [property: JsonPropertyName("clearingCode")] string? ClearingCode,
    [property: JsonPropertyName("clearingText")] string? ClearingText,
    [property: JsonPropertyName("closureDecision")] string? ClosureDecision,
    [property: JsonPropertyName("confirmationDueAt")] DateTimeOffset? ConfirmationDueAt,
    [property: JsonPropertyName("parentTicketId")] long? ParentTicketId,
    [property: JsonPropertyName("openedAt")] DateTimeOffset OpenedAt,
    [property: JsonPropertyName("closedAt")] DateTimeOffset? ClosedAt);

public sealed record AddTicketCommentHttpRequest([property: JsonPropertyName("body")] string Body);

public sealed record TicketCommentResponse(
    [property: JsonPropertyName("commentId")] long CommentId,
    [property: JsonPropertyName("authorType")] string AuthorType,
    [property: JsonPropertyName("authorDisplayName")] string? AuthorDisplayName,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <param name="Decision">Confirmed or Rejected — the only two an ISP may record (TR-PAS-19).</param>
public sealed record RecordClosureDecisionHttpRequest([property: JsonPropertyName("decision")] string Decision);

public sealed record RaiseFollowUpHttpRequest([property: JsonPropertyName("description")] string Description);

public sealed record SubmitServiceChangeHttpRequest(
    [property: JsonPropertyName("lineId")] long LineId,
    [property: JsonPropertyName("changeType")] string ChangeType,
    [property: JsonPropertyName("packageToBe")] string? PackageToBe,
    [property: JsonPropertyName("requestedTerminationDate")] DateOnly? RequestedTerminationDate);

public sealed record ServiceChangeRequestResponse(
    [property: JsonPropertyName("changeId")] long ChangeId,
    [property: JsonPropertyName("publicId")] string PublicId,
    [property: JsonPropertyName("lineId")] long LineId,
    [property: JsonPropertyName("changeType")] string ChangeType,
    [property: JsonPropertyName("packageAsIs")] string PackageAsIs,
    [property: JsonPropertyName("packageToBe")] string? PackageToBe,
    [property: JsonPropertyName("requestedTerminationDate")] DateOnly? RequestedTerminationDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("crmReference")] string? CrmReference,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

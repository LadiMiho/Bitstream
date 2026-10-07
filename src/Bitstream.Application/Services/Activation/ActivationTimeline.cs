using System.Globalization;
using System.Text.Json;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Services.Activation;

/// <summary>Which side an entry on the timeline came from.</summary>
public enum ActivationTimelineKind
{
    /// <summary>A call the portal made to CRM.</summary>
    Outbound,

    /// <summary>An event CRM sent to the portal.</summary>
    Inbound,

    /// <summary>An action a portal user took.</summary>
    Portal
}

public enum ActivationTimelineOutcome
{
    Succeeded,
    Pending,
    Retrying,
    Failed,

    /// <summary>An inbound event the portal refused (wrong order, unknown type, missing field).</summary>
    Rejected,

    /// <summary>An inbound event accepted but ignored because it was older than one already applied.</summary>
    Discarded
}

/// <summary>One step on an activation request's timeline.</summary>
/// <param name="At">When it happened: the message was created, or the user acted.</param>
/// <param name="Kind">Which side it came from.</param>
/// <param name="Title">What happened, e.g. "Create Business Partner in CRM".</param>
/// <param name="Summary">The key data of the step, e.g. the Business Partner or sales order number.</param>
/// <param name="Outcome">How it ended, or whether it is still waiting.</param>
/// <param name="Error">Why it failed or was rejected.</param>
/// <param name="CompletedAt">For a message, when it was answered or finished processing.</param>
/// <param name="Actor">"Portal", "CRM", or the user's full name.</param>
/// <param name="Attempts">Dispatch attempts for an outbound message.</param>
/// <param name="RawRequest">Pretty-printed stored payload; shown only to callers allowed to see it.</param>
/// <param name="RawResponse">Pretty-printed stored response.</param>
public sealed record ActivationTimelineEntry(
    DateTimeOffset At,
    ActivationTimelineKind Kind,
    string Title,
    string? Summary,
    ActivationTimelineOutcome Outcome,
    string Actor,
    string? Error,
    int Attempts,
    DateTimeOffset? CompletedAt,
    string? RawRequest,
    string? RawResponse);

/// <summary>
/// Turns an activation request's <see cref="ActivationHistory"/> into the readable timeline the
/// View drawer shows, oldest first: every call to CRM, every CRM event, and every portal user's
/// action on the request.
/// </summary>
public static class ActivationTimeline
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static IReadOnlyList<ActivationTimelineEntry> Build(ActivationRequest request, ActivationHistory history)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);

        var entries = new List<ActivationTimelineEntry>();

        foreach (var message in history.Messages)
        {
            entries.Add(message.Direction == IntegrationDirection.Outbound ? FromOutbound(message) : FromInbound(message));
        }

        foreach (var audit in history.AuditEntries)
        {
            if (FromAudit(request, audit, history.ActorNames) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return [.. entries.OrderBy(entry => entry.At)];
    }

    private static ActivationTimelineEntry FromOutbound(IntegrationMessage message)
    {
        using var payload = TryParse(message.Payload);
        using var response = TryParse(message.ResponsePayload);

        var (title, summary) = message.InterfaceCode switch
        {
            "INT-CRM-01" => ("Create Business Partner in CRM",
                Read(response, "BusinessPartner") is { } bp ? $"Business Partner {bp}" : null),
            "INT-CRM-02" => ("Create activation ticket in CRM",
                Join(
                    Read(response, "CrmTicketId") is { } ticket ? $"CRM ticket {ticket}" : null,
                    Read(payload, "OfferCode") is { } offer ? $"offer code {offer}" : null)),
            "INT-CRM-10" => ("Send operator confirmation to CRM",
                Join(
                    Read(payload, "Confirmed") switch { "Y" => "Line working: Yes", "N" => "Line working: No", _ => null },
                    Read(payload, "Comment") is { } comment ? $"“{comment}”" : null)),
            _ => ($"{message.InterfaceCode} {message.MessageType}".Trim(), null)
        };

        var outcome = message.Status switch
        {
            IntegrationMessageStatus.Succeeded => ActivationTimelineOutcome.Succeeded,
            IntegrationMessageStatus.Failed => ActivationTimelineOutcome.Retrying,
            IntegrationMessageStatus.DeadLettered => ActivationTimelineOutcome.Failed,
            _ => ActivationTimelineOutcome.Pending
        };

        return new ActivationTimelineEntry(
            message.CreatedAt, ActivationTimelineKind.Outbound, title, summary, outcome, "Portal",
            message.LastError, message.Attempts, message.ProcessedAt, Pretty(message.Payload), Pretty(message.ResponsePayload));
    }

    private static ActivationTimelineEntry FromInbound(IntegrationMessage message)
    {
        using var payload = TryParse(message.Payload);
        var eventPayload = payload is null || !payload.RootElement.TryGetProperty("Payload", out var inner) ? (JsonElement?)null : inner;

        string? Field(string name) =>
            eventPayload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
                ? field.GetString()
                : null;

        var (title, summary) = message.MessageType switch
        {
            "LINE_AVAILABLE" => ("CRM: line available", (string?)"GIS line check found a line"),
            "NO_LINE" => ("CRM: no line", Field("Reason")),
            "SALES_ORDER_OPENED" => ("CRM: sales order opened", Field("SalesOrderId") is { } so ? $"Sales order {so}" : null),
            "LINE_ACTIVATED" => ("CRM: line activated", "Waiting for the operator to confirm the line works"),
            _ => ($"CRM: {message.MessageType}", null)
        };

        var discarded = message.ResponsePayload?.Contains("\"discarded\":true", StringComparison.Ordinal) == true;

        var outcome = message.Status switch
        {
            IntegrationMessageStatus.Succeeded when discarded => ActivationTimelineOutcome.Discarded,
            IntegrationMessageStatus.Succeeded => ActivationTimelineOutcome.Succeeded,
            IntegrationMessageStatus.DeadLettered or IntegrationMessageStatus.Failed => ActivationTimelineOutcome.Rejected,
            _ => ActivationTimelineOutcome.Pending
        };

        var error = outcome == ActivationTimelineOutcome.Discarded
            ? "Ignored: older than an event already applied."
            : message.LastError;

        return new ActivationTimelineEntry(
            message.CreatedAt, ActivationTimelineKind.Inbound, title, summary, outcome, "CRM",
            error, message.Attempts, message.ProcessedAt, Pretty(message.Payload), Pretty(message.ResponsePayload));
    }

    private static ActivationTimelineEntry? FromAudit(ActivationRequest request, AuditLog audit, IReadOnlyDictionary<long, string> actorNames)
    {
        using var newValue = TryParse(audit.NewValue);

        bool? Flag(string name) =>
            newValue is not null && newValue.RootElement.ValueKind == JsonValueKind.Object
                && newValue.RootElement.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

        (string Title, string? Summary)? mapped = audit.ActionCode switch
        {
            "ActivationRequest.Submitted" => ("Request submitted", Join(request.PackageCode, $"{request.ContractDurationMonths} months")),
            "ActivationRequest.GisOutcomeRecorded" => ("GIS outcome recorded in the portal",
                Flag("lineAvailable") switch { true => "Line exists", false => Join("No line", request.StatusReason), _ => null }),
            "ActivationRequest.OperatorConfirmationRecorded" => ("Operator confirmation",
                Join(
                    Flag("working") switch { true => "Line working: Yes", false => "Line working: No", _ => null },
                    request.OperatorComment is { } comment ? $"“{comment}”" : null)),
            "ActivationRequest.ServiceDeskDecisionRecorded" => ("Service desk decision",
                Join(
                    Flag("success") switch { true => "Success", false => "Fail", _ => null },
                    request.ServiceDeskComment is { } comment ? $"“{comment}”" : null)),
            "ActivationRequest.CrmSyncRetried" => ("CRM sync retried", null),
            "ActivationRequest.Closed" => ("Request closed", null),
            _ => null
        };

        if (mapped is not { } entry)
        {
            return null;
        }

        var actor = audit.ActorUserId is { } userId && actorNames.TryGetValue(userId, out var name)
            ? name
            : audit.ActorUserId?.ToString(CultureInfo.InvariantCulture) ?? "Portal";

        return new ActivationTimelineEntry(
            audit.Timestamp, ActivationTimelineKind.Portal, entry.Title, entry.Summary, ActivationTimelineOutcome.Succeeded, actor,
            null, 0, null, null, null);
    }

    private static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Read(JsonDocument? document, string property) =>
        document is not null
        && document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Pretty(string? json)
    {
        using var document = TryParse(json);

        return document is null ? json : JsonSerializer.Serialize(document.RootElement, Indented);
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();

        return present.Length == 0 ? null : string.Join(" · ", present);
    }
}

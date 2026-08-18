using System.Globalization;
using System.Text.Json;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>Thrown when the referenced ticket does not exist. The presentation layer maps this to 404.</summary>
public sealed class TicketClosureNotFoundException : Exception
{
    public TicketClosureNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when the requested action is not valid from the ticket's current state. The presentation layer maps this to 409.</summary>
public sealed class TicketClosureConflictException : Exception
{
    public TicketClosureConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown for a validated business rule violation. The presentation layer maps this to 422.</summary>
public sealed class TicketClosureValidationException : Exception
{
    public TicketClosureValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Implements <see cref="ITicketClosureService"/>: TRD 6.4 (Clearing Code, Confirm/No) and
/// TRD 6.5 (auto-confirmation).
/// <para>
/// TR-PAS-21a to TR-PAS-21c in one paragraph: applying a clearing code starts a Pending ISP
/// Confirmation window <see cref="IWorkingDayCalculator"/> measures in working days from
/// <see cref="ComplaintTicket.ClearingCodeAppliedAt"/>. <see cref="RunAutoConfirmationSweepAsync"/>
/// sends the configured reminders once each, and — if the ISP still has not answered by the
/// due date — auto-confirms with <see cref="ClosureDecision.AutoConfirmed"/>, a value distinct
/// from <see cref="ClosureDecision.Confirmed"/> so the two are never conflated downstream.
/// A persisted ISP decision always wins: once <see cref="ComplaintTicket.ClosureDecision"/> is
/// set, the ticket drops out of the sweep's working set (<c>FindAwaitingConfirmationAsync</c>
/// only returns tickets where it is still null).
/// </para>
/// </summary>
public sealed class TicketClosureService : ITicketClosureService
{
    private readonly IComplaintTicketRepository _ticketRepository;
    private readonly IPublicIdentifierGenerator _identifierGenerator;
    private readonly IIntegrationOutbox _outbox;
    private readonly INotificationService _notificationService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly ICurrentUserContext _currentUser;
    private readonly IWorkingDayCalculator _workingDayCalculator;
    private readonly IOptionsMonitor<TicketClosureOptions> _options;

    public TicketClosureService(
        IComplaintTicketRepository ticketRepository,
        IPublicIdentifierGenerator identifierGenerator,
        IIntegrationOutbox outbox,
        INotificationService notificationService,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICurrentUserContext currentUser,
        IWorkingDayCalculator workingDayCalculator,
        IOptionsMonitor<TicketClosureOptions> options)
    {
        _ticketRepository = ticketRepository;
        _identifierGenerator = identifierGenerator;
        _outbox = outbox;
        _notificationService = notificationService;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _currentUser = currentUser;
        _workingDayCalculator = workingDayCalculator;
        _options = options;
    }

    public async Task ApplyClearingCodeAsync(string ticketPublicId, string clearingCode, string? clearingText, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clearingCode);

        var ticket = await _ticketRepository.FindByPublicIdAsync(ticketPublicId, cancellationToken).ConfigureAwait(false) ??
            throw new TicketClosureNotFoundException($"Ticket '{ticketPublicId}' does not exist.");

        if (string.Equals(ticket.Status, "Closed", StringComparison.Ordinal))
        {
            throw new TicketClosureConflictException($"Ticket '{ticketPublicId}' is already closed.");
        }

        var now = _clock.UtcNow;
        var previousStatus = ticket.Status;

        ticket.ClearingCode = clearingCode;
        ticket.ClearingText = clearingText;
        ticket.Status = "Pending ISP Confirmation";
        ticket.ClearingCodeAppliedAt = now;
        // TR-PAS-21a: the due date, computed once here — the sweep and this method agree on it
        // by construction rather than by recomputing it from configuration on every sweep pass.
        ticket.ConfirmationDueAt = _workingDayCalculator.AddWorkingDays(now, _options.CurrentValue.AutoConfirmAfterWorkingDays);
        ticket.Reminder2SentAt = null;
        ticket.Reminder4SentAt = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ComplaintTicket.ClearingCodeApplied", "ComplaintTicket", ticket.TicketId.ToString(CultureInfo.InvariantCulture),
            $"{{\"status\":\"{previousStatus}\"}}",
            $"{{\"status\":\"Pending ISP Confirmation\",\"clearingCode\":{JsonSerializer.Serialize(clearingCode)},\"confirmationDueAt\":\"{ticket.ConfirmationDueAt:O}\"}}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordIspDecisionAsync(long ticketId, ClosureDecision decision, CancellationToken cancellationToken = default)
    {
        if (decision is not (ClosureDecision.Confirmed or ClosureDecision.Rejected))
        {
            throw new TicketClosureValidationException("Only Confirmed or Rejected may be recorded as an ISP decision.");
        }

        var ticket = await _ticketRepository.FindByIdAsync(ticketId, cancellationToken).ConfigureAwait(false) ??
            throw new TicketClosureNotFoundException($"Ticket {ticketId} does not exist.");

        if (_currentUser.IspId is { } callerIspId && callerIspId != ticket.IspId)
        {
            throw new TicketClosureNotFoundException($"Ticket {ticketId} does not exist.");
        }

        if (!string.Equals(ticket.Status, "Pending ISP Confirmation", StringComparison.Ordinal))
        {
            throw new TicketClosureConflictException($"Ticket {ticketId} is not awaiting a closure decision.");
        }

        var now = _clock.UtcNow;
        ticket.ClosureDecision = decision;
        ticket.ClosureDecisionAt = now;
        ticket.ClosureDecisionBy = _currentUser.UserId;

        if (decision == ClosureDecision.Confirmed)
        {
            ticket.Status = "Closed";
            ticket.ClosedAt = now;
        }
        else
        {
            // TR-PAS-20: "No" instructs CRM to reopen — the ticket goes back into active work,
            // not straight back into another confirmation window.
            ticket.Status = "Reopened";
            ticket.ConfirmationDueAt = null;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await EnqueueClosureDecisionAsync(ticket, decision, systemInitiated: false, systemReason: null, cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ComplaintTicket.ClosureDecisionRecorded", "ComplaintTicket", ticketId.ToString(CultureInfo.InvariantCulture),
            "{\"status\":\"Pending ISP Confirmation\"}", $"{{\"status\":\"{ticket.Status}\",\"decision\":\"{decision}\"}}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAutoConfirmationSweepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;

        if (!options.AutoConfirmationEnabled)
        {
            return;
        }

        var candidates = await _ticketRepository.FindAwaitingConfirmationAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        foreach (var ticket in candidates)
        {
            if (ticket.ClearingCodeAppliedAt is not { } anchor)
            {
                continue;
            }

            await SendDueRemindersAsync(ticket, anchor, now, options, cancellationToken).ConfigureAwait(false);

            // A persisted ISP decision always takes precedence: FindAwaitingConfirmationAsync
            // already excludes tickets with one, but re-checking here is what makes a
            // concurrent ISP decision — recorded after the query ran, before this line — safe:
            // the ticket in hand is a snapshot, so read it again rather than trust the snapshot.
            var current = await _ticketRepository.FindByIdAsync(ticket.TicketId, cancellationToken).ConfigureAwait(false);

            if (current is null || current.ClosureDecision is not null || current.ConfirmationDueAt is not { } dueAt || now < dueAt)
            {
                continue;
            }

            current.ClosureDecision = ClosureDecision.AutoConfirmed;
            current.ClosureDecisionAt = now;
            current.Status = "Closed";
            current.ClosedAt = now;

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await EnqueueClosureDecisionAsync(
                current, ClosureDecision.AutoConfirmed, systemInitiated: true,
                systemReason: $"No ISP decision within {options.AutoConfirmAfterWorkingDays} working days of the clearing code.",
                cancellationToken).ConfigureAwait(false);

            await _auditWriter.WriteAsync(
                "ComplaintTicket.AutoConfirmed", "ComplaintTicket", current.TicketId.ToString(CultureInfo.InvariantCulture),
                "{\"status\":\"Pending ISP Confirmation\"}", "{\"status\":\"Closed\",\"decision\":\"AutoConfirmed\"}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ComplaintTicket> RaiseFollowUpAsync(long originalTicketId, string description, CancellationToken cancellationToken = default)
    {
        var original = await _ticketRepository.FindByIdAsync(originalTicketId, cancellationToken).ConfigureAwait(false) ??
            throw new TicketClosureNotFoundException($"Ticket {originalTicketId} does not exist.");

        if (_currentUser.IspId is { } callerIspId && callerIspId != original.IspId)
        {
            throw new TicketClosureNotFoundException($"Ticket {originalTicketId} does not exist.");
        }

        if (!string.Equals(original.Status, "Closed", StringComparison.Ordinal) || original.ClosedAt is null)
        {
            throw new TicketClosureConflictException($"Ticket {originalTicketId} is not closed; there is nothing to challenge.");
        }

        var now = _clock.UtcNow;
        var challengeDeadline = original.ClosedAt.Value.AddDays(_options.CurrentValue.ChallengeWindowCalendarDays);

        if (now > challengeDeadline)
        {
            // TR-PAS-21f: calendar days, not working days — the challenge window is a fixed
            // grace period after closure, unlike the confirmation window that precedes it.
            throw new TicketClosureConflictException(
                $"The {_options.CurrentValue.ChallengeWindowCalendarDays}-day challenge window for ticket {originalTicketId} closed on {challengeDeadline:yyyy-MM-dd}.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new TicketClosureValidationException("Description is required.");
        }

        var publicId = await _identifierGenerator.NextAsync(IdentifierSeries.ComplaintTicket, cancellationToken).ConfigureAwait(false);

        var followUp = new ComplaintTicket
        {
            PublicId = publicId,
            IspId = original.IspId,
            LineId = original.LineId,
            CategoryL1 = original.CategoryL1,
            CategoryL2 = original.CategoryL2,
            CategoryL3 = original.CategoryL3,
            Description = description,
            Status = "Open",
            ParentTicketId = original.TicketId,
            OpenedAt = now,
            OpenedBy = _currentUser.UserId
        };

        await _ticketRepository.AddAsync(followUp, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ComplaintTicket.FollowUpRaised", "ComplaintTicket", followUp.TicketId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"parentTicketId\":{original.TicketId},\"publicId\":{JsonSerializer.Serialize(publicId)}}}",
            cancellationToken).ConfigureAwait(false);

        return followUp;
    }

    private async Task SendDueRemindersAsync(
        ComplaintTicket ticket, DateTimeOffset anchor, DateTimeOffset now, TicketClosureOptions options, CancellationToken cancellationToken)
    {
        var reminders = options.ReminderAfterWorkingDays;

        // Reminder2SentAt/Reminder4SentAt are exactly two tracking slots, matching the two
        // reminder points TRD 6.5 names (day 2 and day 4) — the default and only configuration
        // this schema supports; a third configured reminder point would need a schema change.
        if (reminders.Count > 0 && ticket.Reminder2SentAt is null && now >= _workingDayCalculator.AddWorkingDays(anchor, reminders[0]))
        {
            ticket.Reminder2SentAt = now;
            await QueueReminderAsync(ticket, reminders[0], cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (reminders.Count > 1 && ticket.Reminder4SentAt is null && now >= _workingDayCalculator.AddWorkingDays(anchor, reminders[1]))
        {
            ticket.Reminder4SentAt = now;
            await QueueReminderAsync(ticket, reminders[1], cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task QueueReminderAsync(ComplaintTicket ticket, int workingDay, CancellationToken cancellationToken)
    {
        await _notificationService.QueueAsync(
            "TICKET_CLOSURE_REMINDER",
            new Dictionary<string, string>
            {
                ["ticketPublicId"] = ticket.PublicId,
                ["workingDay"] = workingDay.ToString(CultureInfo.InvariantCulture)
            },
            "ComplaintTicket", ticket.TicketId, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnqueueClosureDecisionAsync(
        ComplaintTicket ticket, ClosureDecision decision, bool systemInitiated, string? systemReason, CancellationToken cancellationToken)
    {
        if (ticket.CrmTicketId is not { } crmTicketId)
        {
            return;
        }

        var now = _clock.UtcNow;
        var elapsed = ticket.ClearingCodeAppliedAt is { } appliedAt ? now - appliedAt : (TimeSpan?)null;
        var envelope = new IntegrationEnvelope(Guid.NewGuid(), _currentUser.CorrelationId, $"{ticket.PublicId}#closure-{now.Ticks}", now);

        var command = new ClosureDecisionCommand(
            envelope, ticket.PublicId, crmTicketId,
            decision == ClosureDecision.Rejected ? "REJECT" : "CONFIRM",
            systemInitiated, systemReason, elapsed);

        await _outbox.EnqueueOutboundAsync(
            TargetSystem.Crm, "INT-CRM-08", "CLOSURE_DECISION", envelope.IdempotencyKey,
            JsonSerializer.Serialize(command), _currentUser.CorrelationId, ticket.PublicId, cancellationToken)
            .ConfigureAwait(false);
    }
}

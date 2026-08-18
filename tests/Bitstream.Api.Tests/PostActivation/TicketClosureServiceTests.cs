using Bitstream.Api.Tests.Activation;
using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Xunit;

namespace Bitstream.Api.Tests.PostActivation;

/// <summary>
/// TRD 6.5: the auto-confirmation engine, proven by advancing a <see cref="FakeClock"/> and
/// checking the reminder and auto-confirm timing exactly — working days, not calendar days
/// (TR-PAS-21a), each reminder sent exactly once (TR-PAS-21b), and a persisted ISP decision
/// always pre-empting the sweep (TR-PAS-21c/e).
/// <para>
/// The clock is anchored on Monday 2024-01-01T09:00:00Z specifically so every threshold lands
/// on a predictable weekday: with Mon-Fri as the only working days and no holidays, day 2 is
/// Wednesday, day 4 is Friday, and day 5 is the *following* Monday — the Jan 6/7 weekend is not
/// itself a reminder or confirmation trigger, only a reason the count skips two calendar days.
/// </para>
/// </summary>
public sealed class TicketClosureServiceTests
{
    private static readonly DateTimeOffset ClearingCodeAppliedAt = new(2024, 1, 1, 9, 0, 0, TimeSpan.Zero); // Monday
    private static readonly DateTimeOffset Reminder2Due = new(2024, 1, 3, 9, 0, 0, TimeSpan.Zero); // Wednesday, +2 working days
    private static readonly DateTimeOffset Reminder4Due = new(2024, 1, 5, 9, 0, 0, TimeSpan.Zero); // Friday, +4 working days
    private static readonly DateTimeOffset ConfirmationDue = new(2024, 1, 8, 9, 0, 0, TimeSpan.Zero); // Monday, +5 working days (skips the weekend)

    private readonly FakeComplaintTicketRepository _ticketRepository = new();
    private readonly FakeIntegrationOutbox _outbox = new();
    private readonly FakeNotificationService _notificationService = new();
    private readonly FakeAuditWriter _auditWriter = new();
    private readonly FakeCurrentUserContext _currentUser = new() { UserId = 1, RoleName = "IspUser", IspId = 1 };
    private readonly FakeClock _clock = new() { UtcNow = ClearingCodeAppliedAt };

    private TicketClosureService CreateService()
    {
        var workingCalendar = new TestOptionsMonitor<WorkingCalendarOptions>(new WorkingCalendarOptions
        {
            WorkingDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            PublicHolidays = [],
            TimeZoneId = "UTC"
        });

        var closureOptions = new TestOptionsMonitor<TicketClosureOptions>(new TicketClosureOptions
        {
            AutoConfirmationEnabled = true,
            AutoConfirmAfterWorkingDays = 5,
            ReminderAfterWorkingDays = [2, 4],
            ChallengeWindowCalendarDays = 10
        });

        return new TicketClosureService(
            _ticketRepository,
            new FakePublicIdentifierGenerator(),
            _outbox,
            _notificationService,
            new FakeUnitOfWork(),
            _auditWriter,
            _clock,
            _currentUser,
            new WorkingDayCalculator(workingCalendar),
            closureOptions);
    }

    private ComplaintTicket SeedTicket() => new()
    {
        TicketId = 1,
        PublicId = "TKT_1",
        IspId = 1,
        LineId = 1,
        CategoryL1 = "CONNECTIVITY",
        CategoryL2 = "NO_SIGNAL",
        CategoryL3 = "FIBRE_CUT",
        Description = "No signal",
        Status = "Open",
        OpenedAt = ClearingCodeAppliedAt.AddDays(-1)
    };

    [Fact]
    public async Task ApplyClearingCodeAsync_sets_the_confirmation_due_date_five_working_days_out()
    {
        var ticket = SeedTicket();
        _ticketRepository.Tickets[ticket.TicketId] = ticket;
        var service = CreateService();

        await service.ApplyClearingCodeAsync(ticket.PublicId, "RESOLVED", "Fibre spliced");

        Assert.Equal("Pending ISP Confirmation", ticket.Status);
        Assert.Equal(ClearingCodeAppliedAt, ticket.ClearingCodeAppliedAt);
        Assert.Equal(ConfirmationDue, ticket.ConfirmationDueAt);
        Assert.Null(ticket.Reminder2SentAt);
        Assert.Null(ticket.Reminder4SentAt);
    }

    [Fact]
    public async Task Sweep_sends_no_reminder_before_day_2()
    {
        var ticket = await SeedPendingTicketAsync();

        _clock.UtcNow = Reminder2Due.AddMinutes(-1);
        var service = CreateService();
        await service.RunAutoConfirmationSweepAsync();

        Assert.Null(ticket.Reminder2SentAt);
        Assert.Empty(_notificationService.Calls);
    }

    [Fact]
    public async Task Sweep_sends_the_day_2_reminder_exactly_once_at_the_threshold()
    {
        var ticket = await SeedPendingTicketAsync();
        var service = CreateService();

        _clock.UtcNow = Reminder2Due;
        await service.RunAutoConfirmationSweepAsync();

        Assert.Equal(Reminder2Due, ticket.Reminder2SentAt);
        Assert.Single(_notificationService.Calls);

        // Running the sweep again at the same (or a later, pre-day-4) moment must not resend.
        _clock.UtcNow = Reminder2Due.AddHours(1);
        await service.RunAutoConfirmationSweepAsync();
        Assert.Single(_notificationService.Calls);
    }

    [Fact]
    public async Task Sweep_sends_the_day_4_reminder_exactly_once_and_independently_of_day_2()
    {
        var ticket = await SeedPendingTicketAsync();
        var service = CreateService();

        _clock.UtcNow = Reminder4Due.AddMinutes(-1);
        await service.RunAutoConfirmationSweepAsync();
        Assert.Null(ticket.Reminder4SentAt);
        // Day 2 has already passed by this point, so exactly one reminder (day 2) has fired.
        Assert.Single(_notificationService.Calls);

        _clock.UtcNow = Reminder4Due;
        await service.RunAutoConfirmationSweepAsync();
        Assert.Equal(Reminder4Due, ticket.Reminder4SentAt);
        Assert.Equal(2, _notificationService.Calls.Count);

        await service.RunAutoConfirmationSweepAsync();
        Assert.Equal(2, _notificationService.Calls.Count);
    }

    [Fact]
    public async Task Sweep_does_not_auto_confirm_before_day_5()
    {
        var ticket = await SeedPendingTicketAsync();
        var service = CreateService();

        _clock.UtcNow = ConfirmationDue.AddMinutes(-1);
        await service.RunAutoConfirmationSweepAsync();

        Assert.Null(ticket.ClosureDecision);
        Assert.Equal("Pending ISP Confirmation", ticket.Status);
    }

    [Fact]
    public async Task Sweep_auto_confirms_exactly_at_day_5_with_a_decision_distinct_from_ISP_confirmation()
    {
        var ticket = await SeedPendingTicketAsync();
        var service = CreateService();

        _clock.UtcNow = ConfirmationDue;
        await service.RunAutoConfirmationSweepAsync();

        Assert.Equal(ClosureDecision.AutoConfirmed, ticket.ClosureDecision);
        Assert.NotEqual(ClosureDecision.Confirmed, ticket.ClosureDecision);
        Assert.Equal("Closed", ticket.Status);
        Assert.Equal(ConfirmationDue, ticket.ClosedAt);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "ComplaintTicket.AutoConfirmed");

        // Idempotent: a later sweep must not touch an already-decided ticket again.
        var closedAt = ticket.ClosedAt;
        _clock.UtcNow = ConfirmationDue.AddDays(1);
        await service.RunAutoConfirmationSweepAsync();
        Assert.Equal(closedAt, ticket.ClosedAt);
    }

    [Fact]
    public async Task A_persisted_ISP_decision_pre_empts_auto_confirmation_even_after_the_due_date()
    {
        var ticket = await SeedPendingTicketAsync();
        var service = CreateService();

        _clock.UtcNow = Reminder2Due;
        await service.RecordIspDecisionAsync(ticket.TicketId, ClosureDecision.Confirmed);

        Assert.Equal(ClosureDecision.Confirmed, ticket.ClosureDecision);
        Assert.Equal("Closed", ticket.Status);

        // Well past the auto-confirm due date now — the sweep must leave the ISP's decision alone.
        _clock.UtcNow = ConfirmationDue.AddDays(1);
        await service.RunAutoConfirmationSweepAsync();

        Assert.Equal(ClosureDecision.Confirmed, ticket.ClosureDecision);
        Assert.Equal(Reminder2Due, ticket.ClosureDecisionAt);
    }

    [Fact]
    public async Task Rejected_reopens_the_ticket_instead_of_closing_it()
    {
        var ticket = await SeedPendingTicketAsync();
        var service = CreateService();

        await service.RecordIspDecisionAsync(ticket.TicketId, ClosureDecision.Rejected);

        Assert.Equal(ClosureDecision.Rejected, ticket.ClosureDecision);
        Assert.Equal("Reopened", ticket.Status);
        Assert.Null(ticket.ConfirmationDueAt);
    }

    [Fact]
    public async Task RecordIspDecisionAsync_rejects_a_ticket_not_awaiting_confirmation()
    {
        var ticket = SeedTicket(); // still "Open" — no clearing code applied
        _ticketRepository.Tickets[ticket.TicketId] = ticket;
        var service = CreateService();

        await Assert.ThrowsAsync<TicketClosureConflictException>(() => service.RecordIspDecisionAsync(ticket.TicketId, ClosureDecision.Confirmed));
    }

    private async Task<ComplaintTicket> SeedPendingTicketAsync()
    {
        var ticket = SeedTicket();
        _ticketRepository.Tickets[ticket.TicketId] = ticket;

        var service = CreateService();
        _clock.UtcNow = ClearingCodeAppliedAt;
        await service.ApplyClearingCodeAsync(ticket.PublicId, "RESOLVED", null);

        return ticket;
    }
}

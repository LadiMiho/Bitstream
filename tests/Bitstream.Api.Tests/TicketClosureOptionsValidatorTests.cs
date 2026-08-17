using Bitstream.Application.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bitstream.Api.Tests;

/// <summary>
/// TR-PAS-21b states that disabling reminders entirely must not be possible while
/// auto-confirmation is enabled. Since the reminders are configuration (TR-ARC-06), the rule is
/// only real if the configuration refuses to load without them — otherwise a settings change
/// could start closing ISPs out silently, which is the objection the whole mechanism exists to
/// answer.
/// </summary>
public sealed class TicketClosureOptionsValidatorTests
{
    private readonly TicketClosureOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_mechanism_proposed_in_the_TRD()
    {
        // TRD 6.5: reminders at day 2 and day 4, auto-confirmation at day 5.
        var result = _validator.Validate(null, new TicketClosureOptions
        {
            AutoConfirmationEnabled = true,
            AutoConfirmAfterWorkingDays = 5,
            ReminderAfterWorkingDays = [2, 4],
            ChallengeWindowCalendarDays = 10
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Rejects_auto_confirmation_with_no_reminders()
    {
        var result = _validator.Validate(null, new TicketClosureOptions
        {
            AutoConfirmationEnabled = true,
            AutoConfirmAfterWorkingDays = 5,
            ReminderAfterWorkingDays = []
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("TR-PAS-21b", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_reminder_that_arrives_at_or_after_the_deadline()
    {
        // A reminder sent on the day the ticket auto-confirms warns nobody.
        var result = _validator.Validate(null, new TicketClosureOptions
        {
            AutoConfirmationEnabled = true,
            AutoConfirmAfterWorkingDays = 5,
            ReminderAfterWorkingDays = [2, 5]
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Allows_no_reminders_when_auto_confirmation_is_switched_off()
    {
        // Nothing closes on its own, so there is nothing to warn about.
        var result = _validator.Validate(null, new TicketClosureOptions
        {
            AutoConfirmationEnabled = false,
            ReminderAfterWorkingDays = []
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Rejects_unordered_or_duplicated_reminders()
    {
        Assert.True(_validator.Validate(null, new TicketClosureOptions
        {
            AutoConfirmAfterWorkingDays = 5,
            ReminderAfterWorkingDays = [4, 2]
        }).Failed);

        Assert.True(_validator.Validate(null, new TicketClosureOptions
        {
            AutoConfirmAfterWorkingDays = 5,
            ReminderAfterWorkingDays = [2, 2]
        }).Failed);
    }
}

/// <summary>
/// TR-DAT-02d fixes the identifier format as <c>^[A-Z]+_[0-9]+$</c> and TR-DAT-06 requires
/// complaint tickets to use a distinguishable series. Both are configuration values
/// (TRD 11.4 open item 2), so both are checked where they are configured.
/// </summary>
public sealed class IdentifierOptionsValidatorTests
{
    private readonly IdentifierOptionsValidator _validator = new();

    [Fact]
    public void Accepts_unset_prefixes()
    {
        // The agreed values are an open item; a developer must still be able to start the host.
        Assert.True(_validator.Validate(null, new IdentifierOptions()).Succeeded);
    }

    [Theory]
    [InlineData("isp")]
    [InlineData("ISP1")]
    [InlineData("ISP_")]
    [InlineData("IS P")]
    public void Rejects_a_prefix_that_cannot_satisfy_the_agreed_pattern(string prefix)
    {
        var result = _validator.Validate(null, new IdentifierOptions { ActivationRequestPrefix = prefix });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Rejects_identical_activation_and_ticket_prefixes()
    {
        var result = _validator.Validate(null, new IdentifierOptions
        {
            ActivationRequestPrefix = "ISP",
            ComplaintTicketPrefix = "ISP"
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("TR-DAT-06", StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_distinguishable_prefixes()
    {
        var result = _validator.Validate(null, new IdentifierOptions
        {
            ActivationRequestPrefix = "ISP",
            ComplaintTicketPrefix = "TKT",
            ServiceChangeRequestPrefix = "SCR"
        });

        Assert.True(result.Succeeded);
    }
}

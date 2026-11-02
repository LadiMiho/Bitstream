using Bitstream.Application.Services.Notifications;
using Xunit;

namespace Bitstream.Api.Tests.Notifications;

/// <summary>How a notification email's template is chosen and filled.</summary>
public sealed class EmailNotificationRendererTests
{
    private static Dictionary<string, string?> Values(string? reason = null, string? link = "https://portal.example.com/ActivationRequests?open=TRING001") => new()
    {
        ["RecipientName"] = "Anna <Hoxha>",
        ["RequestType"] = "Activation request",
        ["RequestId"] = "TRING001",
        ["IspName"] = "Tring",
        ["Status"] = "Rejected — No Line",
        ["PreviousStatus"] = "Waiting GIS Verification",
        ["Change"] = "has moved from Waiting GIS Verification to Rejected — No Line",
        ["Reason"] = reason,
        ["Link"] = link
    };

    [Fact]
    public void The_most_specific_template_wins_and_the_default_fills_what_it_leaves_out()
    {
        var options = new EmailNotificationOptions();
        options.Templates["ActivationRequest.WaitingForServiceDesk"] = new EmailTemplate { Subject = "For everyone", Body = "Body for everyone" };
        options.Templates["ActivationRequest.WaitingForServiceDesk.ServiceDesk"] = new EmailTemplate { Subject = "For the service desk" };

        var desk = EmailNotificationRenderer.Resolve(options, "ActivationRequest", "WaitingForServiceDesk", "ServiceDesk");
        Assert.Equal("ActivationRequest.WaitingForServiceDesk.ServiceDesk", desk.Key);
        Assert.Equal("For the service desk", desk.Template.Subject);
        Assert.Equal(options.Default.Body, desk.Template.Body);

        var isp = EmailNotificationRenderer.Resolve(options, "ActivationRequest", "WaitingForServiceDesk", "IspUsers");
        Assert.Equal("For everyone", isp.Template.Subject);

        Assert.Equal("Default", EmailNotificationRenderer.Resolve(options, "ActivationRequest", "Completed", "IspUsers").Key);
    }

    [Fact]
    public void A_text_mail_fills_the_placeholders_and_leaves_out_a_line_with_no_value()
    {
        var options = new EmailNotificationOptions();

        var withoutReason = EmailNotificationRenderer.Render("Default", options.Default, Values(), html: false);
        Assert.Equal("Activation request TRING001: Rejected — No Line", withoutReason.Subject);
        Assert.StartsWith("Hello Anna <Hoxha>,\n\nActivation request TRING001 (Tring) has moved from Waiting GIS Verification to Rejected — No Line.", withoutReason.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Reason:", withoutReason.Body, StringComparison.Ordinal);
        Assert.Contains("Open the request: https://portal.example.com/ActivationRequests?open=TRING001", withoutReason.Body, StringComparison.Ordinal);

        var withReason = EmailNotificationRenderer.Render("Default", options.Default, Values("No fibre in this street"), html: false);
        Assert.Contains("Reason: No fibre in this street", withReason.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_HTML_mail_encodes_the_values_and_adds_the_open_request_button()
    {
        var options = new EmailNotificationOptions();

        var mail = EmailNotificationRenderer.Render("Default", options.Default, Values("<b>No fibre</b>"), html: true);

        Assert.StartsWith("<!DOCTYPE html>", mail.Body, StringComparison.Ordinal);
        Assert.Contains("Hello Anna &lt;Hoxha&gt;,", mail.Body, StringComparison.Ordinal);
        Assert.Contains("<strong>&lt;b&gt;No fibre&lt;/b&gt;</strong>", mail.Body, StringComparison.Ordinal);
        Assert.Contains("href=\"https://portal.example.com/ActivationRequests?open=TRING001\"", mail.Body, StringComparison.Ordinal);
        Assert.Contains(">Open request</a>", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>No fibre</b>", mail.Body, StringComparison.Ordinal);

        var noLink = EmailNotificationRenderer.Render("Default", options.Default, Values(link: null), html: true);
        Assert.DoesNotContain("Open request", noLink.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_placeholder_is_left_as_written()
    {
        var mail = EmailNotificationRenderer.Render("x", new EmailTemplate { Subject = "{Nope} {RequestId}", Body = "Text {Nope}" }, Values(), html: false);

        Assert.Equal("{Nope} TRING001", mail.Subject);
        Assert.Contains("Text {Nope}", mail.Body, StringComparison.Ordinal);
    }
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bitstream.Application.Services.Notifications;

/// <summary>A subject and a body with {Placeholders}; either may be left empty to use the default's.</summary>
public sealed class EmailTemplate
{
    public string? Subject { get; set; }

    /// <summary>Plain text: a blank line starts a new paragraph. A line holding a placeholder with no value (e.g. no {Reason}) is left out.</summary>
    public string? Body { get; set; }
}

/// <summary>
/// The email notifications of request status changes (<c>Notifications:Email</c>). Whether a
/// status change is emailed, and to whom, is set on the Notification settings screen; this only
/// says how the mail reads and how it is sent.
/// </summary>
public sealed class EmailNotificationOptions
{
    public const string SectionName = "Notifications:Email";

    /// <summary>Off: no notification email is queued or sent (the in-app notifications are unaffected).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The portal's public address, e.g. https://bitstream.one.al; the mails' "Open request" link starts with it. Empty: no link.</summary>
    public string? PortalBaseUrl { get; set; }

    /// <summary><c>Html</c> (a branded layout around the text) or <c>Text</c>.</summary>
    public string Format { get; set; } = "Html";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int BatchSize { get; set; } = 20;

    /// <summary>Sending attempts before a mail is given up as Failed.</summary>
    public int MaxAttempts { get; set; } = 5;

    public EmailTemplate Default { get; set; } = new()
    {
        Subject = "{RequestType} {RequestId}: {Status}",
        Body = "Hello {RecipientName},\n\n{RequestType} {RequestId} ({IspName}) {Change}.\n\nReason: {Reason}"
    };

    /// <summary>
    /// Wording for particular status changes, keyed <c>{EntityType}.{Status}</c> (e.g.
    /// <c>ActivationRequest.RejectedNoLine</c>) or, for one audience only,
    /// <c>{EntityType}.{Status}.{Audience}</c> (e.g. <c>ActivationRequest.WaitingForServiceDesk.ServiceDesk</c>).
    /// The most specific key wins; anything it leaves empty comes from <see cref="Default"/>.
    /// </summary>
    public Dictionary<string, EmailTemplate> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsHtml => !string.Equals(Format, "Text", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Checks <see cref="EmailNotificationOptions"/> at start-up.</summary>
public sealed class EmailNotificationOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<EmailNotificationOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, EmailNotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.PollInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("Notifications:Email:PollInterval must be at least one second.");
        }

        if (options.BatchSize < 1 || options.MaxAttempts < 1)
        {
            failures.Add("Notifications:Email:BatchSize and MaxAttempts must be at least 1.");
        }

        if (!string.Equals(options.Format, "Html", StringComparison.OrdinalIgnoreCase) && !string.Equals(options.Format, "Text", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Notifications:Email:Format must be Html or Text. Configured: '{options.Format}'.");
        }

        if (!string.IsNullOrWhiteSpace(options.PortalBaseUrl) && !Uri.TryCreate(options.PortalBaseUrl, UriKind.Absolute, out _))
        {
            failures.Add($"Notifications:Email:PortalBaseUrl must be an absolute address, e.g. https://bitstream.one.al. Configured: '{options.PortalBaseUrl}'.");
        }

        return failures.Count == 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Success
            : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>A rendered notification mail: the template key it came from, its subject, and its body in the configured format.</summary>
public sealed record RenderedEmail(string TemplateKey, string Subject, string Body);

/// <summary>
/// Fills a notification mail's template. Placeholders: {RecipientName}, {RequestType},
/// {RequestId}, {IspName}, {Status}, {PreviousStatus}, {Change}, {Reason}, {Link}. Values are
/// HTML-encoded in an HTML mail, which gets the portal's colours and an "Open request" button.
/// </summary>
public static partial class EmailNotificationRenderer
{
    private const string Footer = "This is an automatic message from the Bitstream Portal. Please do not reply to it.";

    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    /// <summary>The template for a status change and audience, most specific first, with the default filling any gap.</summary>
    public static (string Key, EmailTemplate Template) Resolve(EmailNotificationOptions options, string entityType, string status, string audience)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var key in new[] { $"{entityType}.{status}.{audience}", $"{entityType}.{status}" })
        {
            if (options.Templates.TryGetValue(key, out var specific))
            {
                return (key, new EmailTemplate
                {
                    Subject = string.IsNullOrWhiteSpace(specific.Subject) ? options.Default.Subject : specific.Subject,
                    Body = string.IsNullOrWhiteSpace(specific.Body) ? options.Default.Body : specific.Body
                });
            }
        }

        return ("Default", options.Default);
    }

    public static RenderedEmail Render(string key, EmailTemplate template, IReadOnlyDictionary<string, string?> values, bool html)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var subject = PlaceholderPattern().Replace(template.Subject ?? string.Empty, match => Fill(values, match)).Trim();
        var lines = (template.Body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !PlaceholderPattern().Matches(line).Any(match => Known(values, match.Groups[1].Value) && string.IsNullOrWhiteSpace(Value(values, match.Groups[1].Value))))
            .ToList();
        var link = Value(values, "Link");

        return new RenderedEmail(key, subject, html ? RenderHtml(lines, values, link, subject) : RenderText(lines, values, link));
    }

    private static string RenderText(List<string> lines, IReadOnlyDictionary<string, string?> values, string? link)
    {
        var text = new StringBuilder();
        text.Append(string.Join('\n', lines.Select(line => PlaceholderPattern().Replace(line, match => Fill(values, match)))).Trim());

        if (!string.IsNullOrWhiteSpace(link))
        {
            text.Append("\n\nOpen the request: ").Append(link);
        }

        text.Append("\n\n—\n").Append(Footer);
        return text.ToString();
    }

    private static string RenderHtml(List<string> lines, IReadOnlyDictionary<string, string?> values, string? link, string subject)
    {
        var paragraphs = new List<List<string>> { new() };

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (paragraphs[^1].Count > 0)
                {
                    paragraphs.Add([]);
                }

                continue;
            }

            paragraphs[^1].Add(HtmlLine(line, values));
        }

        var content = new StringBuilder();
        foreach (var paragraph in paragraphs.Where(p => p.Count > 0))
        {
            content.Append("<p style=\"margin:0 0 14px 0;\">").Append(string.Join("<br>", paragraph)).Append("</p>");
        }

        var button = string.IsNullOrWhiteSpace(link)
            ? string.Empty
            : "<tr><td style=\"padding:4px 28px 28px 28px;\">" +
              $"<a href=\"{WebUtility.HtmlEncode(link)}\" style=\"display:inline-block;background:#66318E;color:#ffffff;text-decoration:none;" +
              "font-weight:600;font-size:14px;padding:10px 20px;border-radius:6px;\">Open request</a></td></tr>";

        return
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            $"<title>{WebUtility.HtmlEncode(subject)}</title></head>" +
            "<body style=\"margin:0;padding:0;background:#f6f4f8;font-family:'Segoe UI',Arial,sans-serif;color:#2b2233;\">" +
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f6f4f8;padding:24px 12px;\"><tr><td align=\"center\">" +
            "<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;width:100%;background:#ffffff;border:1px solid #e6e1ea;border-radius:8px;overflow:hidden;\">" +
            "<tr><td style=\"background:#66318E;padding:16px 28px;color:#ffffff;font-size:18px;font-weight:600;\">Bitstream Portal</td></tr>" +
            "<tr><td style=\"height:3px;line-height:3px;font-size:0;background:#F4B01C;\">&nbsp;</td></tr>" +
            $"<tr><td style=\"padding:28px 28px 14px 28px;font-size:14px;line-height:1.6;\">{content}</td></tr>" +
            button +
            $"<tr><td style=\"padding:16px 28px;border-top:1px solid #eeeaf1;font-size:12px;line-height:1.5;color:#7a6f85;\">{WebUtility.HtmlEncode(Footer)}</td></tr>" +
            "</table></td></tr></table></body></html>";
    }

    /// <summary>One template line: its text HTML-encoded, its placeholders filled with HTML-encoded values ({Link} as a link).</summary>
    private static string HtmlLine(string line, IReadOnlyDictionary<string, string?> values)
    {
        var html = new StringBuilder();
        var position = 0;

        foreach (Match match in PlaceholderPattern().Matches(line))
        {
            html.Append(WebUtility.HtmlEncode(line[position..match.Index]));
            var name = match.Groups[1].Value;
            var value = Value(values, name);

            html.Append(HtmlValue(name, value, Known(values, name), match.Value));
            position = match.Index + match.Length;
        }

        html.Append(WebUtility.HtmlEncode(line[position..]));
        return html.ToString();
    }

    /// <summary>A placeholder in an HTML mail: an unknown one as written, {Link} as a link, {Reason} and {RequestId} in bold.</summary>
    private static string HtmlValue(string name, string? value, bool known, string written)
    {
        if (!known)
        {
            return WebUtility.HtmlEncode(written);
        }

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var encoded = WebUtility.HtmlEncode(value);

        if (string.Equals(name, "Link", StringComparison.OrdinalIgnoreCase))
        {
            return $"<a href=\"{encoded}\" style=\"color:#66318E;\">{encoded}</a>";
        }

        return string.Equals(name, "Reason", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RequestId", StringComparison.OrdinalIgnoreCase)
            ? $"<strong>{encoded}</strong>"
            : encoded;
    }

    /// <summary>A known placeholder's value (empty when it has none); an unknown one stays as written.</summary>
    private static string Fill(IReadOnlyDictionary<string, string?> values, Match match) =>
        Known(values, match.Groups[1].Value) ? Value(values, match.Groups[1].Value) ?? string.Empty : match.Value;

    private static bool Known(IReadOnlyDictionary<string, string?> values, string name) =>
        values.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(IReadOnlyDictionary<string, string?> values, string name) =>
        values.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

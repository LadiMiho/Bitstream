using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bitstream.Application.Abstractions.Integration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Integration.Mail;

/// <summary>Configuration of the HTTP email gateway (<c>Integration:Email</c>).</summary>
public sealed class EmailGatewayOptions
{
    public const string SectionName = "Integration:Email";

    public const string HttpClientName = "EmailGateway";

    /// <summary>The gateway's address, e.g. http://10.9.4.9:9941.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Path of the send call, relative to <see cref="BaseUrl"/>.</summary>
    public string SendEndpoint { get; set; } = "/api/SendMail/send";

    /// <summary>Sent as a Bearer token when set. Keep it out of source control (environment variable or the server's appsettings).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The sender address (fromEmail).</summary>
    public string? Sender { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Logs each request (body length only, never the body) and the gateway's answer.</summary>
    public bool LogPayloads { get; set; }

    /// <summary>When true, the plain-text version of a message is sent instead of its HTML.</summary>
    public bool PreferPlainText { get; set; }

    /// <summary>Test servers: every message goes to this address instead of its recipient, whose address is put in the subject (TR-NTF-07).</summary>
    public string? RedirectAllMailTo { get; set; }

    public bool IsConfigured => BaseUrl is not null && !string.IsNullOrWhiteSpace(SendEndpoint) && !string.IsNullOrWhiteSpace(Sender);
}

/// <summary>
/// Sends mail through the HTTP email gateway (<c>POST {BaseUrl}{SendEndpoint}</c>, TRD 7.1
/// INT-MAIL-01): one call per recipient, with the body <c>{fromEmail, toEmail, subject, body,
/// ccEmails, attachments}</c>. Used for the email verification code at sign-in and for the email
/// notifications of request status changes.
/// <para>
/// A gateway that is not configured, or answers 4xx, is a business rejection (sending again will
/// not help); a 5xx, a timeout or no connection is a technical failure the notification
/// dispatcher retries. Recipient addresses are masked in the log.
/// </para>
/// </summary>
public sealed class HttpEmailGateway : IEmailGateway
{
    private const int MaxLoggedResponseLength = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<EmailGatewayOptions> _options;
    private readonly ILogger<HttpEmailGateway> _logger;

    public HttpEmailGateway(IHttpClientFactory httpClientFactory, IOptionsMonitor<EmailGatewayOptions> options, ILogger<HttpEmailGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<IntegrationResult<bool>> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = _options.CurrentValue;

        if (!options.IsConfigured)
        {
            _logger.LogError("An email was to be sent but the email gateway is not configured (Integration:Email:BaseUrl, SendEndpoint, Sender).");
            return IntegrationResult<bool>.BusinessRejection("provider_not_configured", "The email gateway is not configured.");
        }

        if (message.To.Count == 0)
        {
            return IntegrationResult<bool>.BusinessRejection("no_recipient", "The message has no recipient.");
        }

        var body = options.PreferPlainText && !string.IsNullOrEmpty(message.PlainTextBody)
            ? message.PlainTextBody
            : string.IsNullOrEmpty(message.HtmlBody) ? message.PlainTextBody : message.HtmlBody;

        foreach (var recipient in message.To)
        {
            var result = await SendOneAsync(options, message, recipient, body, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return IntegrationResult<bool>.Success(true);
    }

    private async Task<IntegrationResult<bool>> SendOneAsync(
        EmailGatewayOptions options, EmailMessage message, string recipient, string body, CancellationToken cancellationToken)
    {
        var redirect = string.IsNullOrWhiteSpace(options.RedirectAllMailTo) ? null : options.RedirectAllMailTo.Trim();
        var payload = new SendEmailRequest
        {
            FromEmail = options.Sender!,
            ToEmail = redirect ?? recipient,
            Subject = redirect is null ? message.Subject : $"[to {recipient}] {message.Subject}",
            Body = body
        };

        var masked = Mask(payload.ToEmail);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.SendEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        var client = _httpClientFactory.CreateClient(EmailGatewayOptions.HttpClientName);
        var target = client.BaseAddress is null ? options.SendEndpoint : new Uri(client.BaseAddress, options.SendEndpoint).ToString();

        if (options.LogPayloads)
        {
            _logger.LogInformation(
                "Email gateway request to {EmailGatewayUrl} for {MaskedRecipient}: subject '{Subject}', body {BodyLength} characters",
                target, masked, payload.Subject, payload.Body.Length);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            answer = answer.Length > MaxLoggedResponseLength ? answer[..MaxLoggedResponseLength] : answer;

            if (options.LogPayloads)
            {
                _logger.LogInformation(
                    "Email gateway answered HTTP {HttpStatusCode} for {MaskedRecipient}: {EmailResponseBody}", (int)response.StatusCode, masked, answer);
            }

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Email gateway {EmailGatewayUrl} accepted the message for {MaskedRecipient}", target, masked);
                return IntegrationResult<bool>.Success(true);
            }

            var error = $"The email gateway returned HTTP {(int)response.StatusCode}{(string.IsNullOrWhiteSpace(answer) ? "" : $": {answer}")}";
            _logger.LogWarning("Email gateway {EmailGatewayUrl} refused the message for {MaskedRecipient} with HTTP {HttpStatusCode}", target, masked, (int)response.StatusCode);

            return (int)response.StatusCode >= 500 || (int)response.StatusCode == 429
                ? IntegrationResult<bool>.TechnicalFailure(error, "provider_http_error")
                : IntegrationResult<bool>.BusinessRejection("provider_http_error", error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Email gateway {EmailGatewayUrl} timed out after {TimeoutSeconds}s sending to {MaskedRecipient}", target, options.TimeoutSeconds, masked);
            return IntegrationResult<bool>.Timeout($"The email gateway did not answer within {options.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Email gateway {EmailGatewayUrl} could not be reached sending to {MaskedRecipient}", target, masked);
            return IntegrationResult<bool>.TechnicalFailure($"The email gateway could not be reached: {exception.Message}", "provider_unreachable");
        }
    }

    /// <summary>a***@one.al: enough to recognise the mailbox in a log without writing the address.</summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }

    /// <summary>The gateway's request body: fromEmail, toEmail, subject, body, ccEmails, attachments.</summary>
    internal sealed record SendEmailRequest
    {
        public required string FromEmail { get; init; }

        public required string ToEmail { get; init; }

        public required string Subject { get; init; }

        public required string Body { get; init; }

        /// <summary>Always empty: each recipient gets their own message.</summary>
        public IReadOnlyList<string> CcEmails { get; } = [];

        /// <summary>Always empty: these messages have nothing to attach.</summary>
        public IReadOnlyList<object> Attachments { get; } = [];
    }
}

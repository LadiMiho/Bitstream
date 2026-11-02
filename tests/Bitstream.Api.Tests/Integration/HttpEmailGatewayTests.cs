using System.Net;
using System.Text.Json;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Infrastructure.Integration.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bitstream.Api.Tests.Integration;

/// <summary>INT-MAIL-01: the HTTP email gateway call (POST /api/SendMail/send).</summary>
public sealed class HttpEmailGatewayTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"m-1\"}") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Respond();
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://10.9.4.9:9941") };
    }

    private sealed class StaticOptions(EmailGatewayOptions value) : IOptionsMonitor<EmailGatewayOptions>
    {
        public EmailGatewayOptions CurrentValue => value;

        public EmailGatewayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EmailGatewayOptions, string?> listener) => null;
    }

    private static HttpEmailGateway Gateway(RecordingHandler handler, Action<EmailGatewayOptions>? configure = null)
    {
        var options = new EmailGatewayOptions { BaseUrl = new Uri("http://10.9.4.9:9941"), Sender = "no-reply@one.al", ApiKey = "secret-key" };
        configure?.Invoke(options);
        return new HttpEmailGateway(new SingleClientFactory(handler), new StaticOptions(options), NullLogger<HttpEmailGateway>.Instance);
    }

    private static EmailMessage Message(params string[] to) => new(
        new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "key-1", DateTimeOffset.UtcNow), to, [], "TRING001: Line Available", "<p>html</p>", "text");

    [Fact]
    public async Task Each_recipient_gets_one_call_with_the_gateways_body_and_the_bearer_key()
    {
        var handler = new RecordingHandler();

        var result = await Gateway(handler).SendAsync(Message("anna@tring.al", "ben@tring.al"));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Calls.Count);

        var (request, body) = handler.Calls[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://10.9.4.9:9941/api/SendMail/send", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", request.Headers.Authorization.Parameter);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(["fromEmail", "toEmail", "subject", "body", "ccEmails", "attachments"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("no-reply@one.al", root.GetProperty("fromEmail").GetString());
        Assert.Equal("anna@tring.al", root.GetProperty("toEmail").GetString());
        Assert.Equal("TRING001: Line Available", root.GetProperty("subject").GetString());
        Assert.Equal("<p>html</p>", root.GetProperty("body").GetString());
        Assert.Equal(0, root.GetProperty("ccEmails").GetArrayLength());

        using var second = JsonDocument.Parse(handler.Calls[1].Body);
        Assert.Equal("ben@tring.al", second.RootElement.GetProperty("toEmail").GetString());
    }

    [Fact]
    public async Task Plain_text_can_be_preferred_and_a_test_server_redirects_every_mail()
    {
        var handler = new RecordingHandler();

        await Gateway(handler, o => { o.PreferPlainText = true; o.RedirectAllMailTo = "test@one.al"; o.ApiKey = ""; }).SendAsync(Message("anna@tring.al"));

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Null(request.Headers.Authorization);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("test@one.al", json.RootElement.GetProperty("toEmail").GetString());
        Assert.Equal("[to anna@tring.al] TRING001: Line Available", json.RootElement.GetProperty("subject").GetString());
        Assert.Equal("text", json.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, IntegrationOutcome.BusinessRejection)]
    [InlineData(HttpStatusCode.InternalServerError, IntegrationOutcome.TechnicalFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, IntegrationOutcome.TechnicalFailure)]
    public async Task A_refusal_is_not_retried_and_a_server_error_is(HttpStatusCode status, IntegrationOutcome expected)
    {
        var handler = new RecordingHandler { Respond = () => new HttpResponseMessage(status) { Content = new StringContent("nope") } };

        var result = await Gateway(handler).SendAsync(Message("anna@tring.al"));

        Assert.Equal(expected, result.Outcome);
        Assert.Contains($"HTTP {(int)status}: nope", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_gateway_is_a_technical_failure_and_an_unconfigured_one_a_refusal()
    {
        var down = new RecordingHandler { Respond = () => throw new HttpRequestException("connection refused") };
        Assert.Equal(IntegrationOutcome.TechnicalFailure, (await Gateway(down).SendAsync(Message("anna@tring.al"))).Outcome);

        var unconfigured = await Gateway(new RecordingHandler(), o => o.Sender = null).SendAsync(Message("anna@tring.al"));
        Assert.Equal(IntegrationOutcome.BusinessRejection, unconfigured.Outcome);
        Assert.Equal("provider_not_configured", unconfigured.ErrorCode);
    }

    [Fact]
    public void Addresses_are_masked_in_the_log()
    {
        Assert.Equal("a***@tring.al", HttpEmailGateway.Mask("anna@tring.al"));
    }
}

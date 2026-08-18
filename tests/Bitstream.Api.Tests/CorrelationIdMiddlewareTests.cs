using Bitstream.Application;
using Bitstream.Application.Abstractions;
using Bitstream.Hosting.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Bitstream.Api.Tests;

/// <summary>
/// TR-ARC-04: every request is assigned a correlation ID, propagated to downstream calls and
/// written to every log entry.
/// </summary>
public sealed class CorrelationIdMiddlewareTests
{
    private readonly CorrelationContext _correlationContext = new();
    private readonly TestLogger<CorrelationIdMiddleware> _logger = new();

    [Fact]
    public async Task Generates_a_correlation_id_when_the_caller_supplies_none()
    {
        var context = new DefaultHttpContext();

        await InvokeAsync(context);

        var assigned = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.HeaderName]);
        Assert.False(string.IsNullOrWhiteSpace(assigned));
        Assert.Equal(assigned, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task Honours_a_well_formed_inbound_correlation_id()
    {
        // This is what lets a CRM-originated event be traced from CRM's logs through the portal's.
        const string supplied = "crm-8891203-a4f1";

        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;

        await InvokeAsync(context);

        Assert.Equal(supplied, context.Items[CorrelationIdMiddleware.HeaderName]);
        Assert.Equal(supplied, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Theory]
    // Whitespace and control characters would be written verbatim into log fields.
    [InlineData("has spaces")]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    [InlineData("semi;colon")]
    [InlineData("<script>")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_malformed_inbound_correlation_id(string supplied)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;

        await InvokeAsync(context);

        var assigned = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.HeaderName]);
        Assert.NotEqual(supplied, assigned);
        Assert.True(CorrelationIdMiddleware.IsAcceptable(assigned));
    }

    [Fact]
    public async Task Rejects_an_oversized_inbound_correlation_id()
    {
        var oversized = new string('a', CorrelationIdMiddleware.MaxLength + 1);

        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = oversized;

        await InvokeAsync(context);

        Assert.NotEqual(oversized, context.Items[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task Publishes_the_correlation_id_as_ambient_state_for_the_rest_of_the_pipeline()
    {
        // The adapters read it from here to put on outbound calls (TR-INT-02), so it has to be
        // visible to everything the request goes on to do.
        const string supplied = "trace-0001";

        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;

        string? observedDownstream = null;

        var middleware = new CorrelationIdMiddleware(
            _ =>
            {
                observedDownstream = _correlationContext.CorrelationId;
                return Task.CompletedTask;
            },
            _correlationContext,
            _logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(supplied, observedDownstream);
    }

    [Fact]
    public async Task Restores_the_previous_ambient_value_after_the_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "request-scoped";

        await InvokeAsync(context);

        // Leaking a finished request's ID into whatever runs next would misattribute its logs.
        Assert.NotEqual("request-scoped", _correlationContext.CorrelationId);
    }

    [Fact]
    public async Task Opens_a_logging_scope_carrying_the_correlation_id()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "scope-check";

        await InvokeAsync(context);

        var scope = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(Assert.Single(_logger.Scopes));
        Assert.Equal("scope-check", scope["CorrelationId"]);
    }

    [Fact]
    public async Task Sets_the_response_header_before_the_response_starts()
    {
        // Headers cannot be added once the response has begun, so the middleware must set it
        // before calling downstream, not after.
        var context = new DefaultHttpContext();
        string? headerSeenDownstream = null;

        var middleware = new CorrelationIdMiddleware(
            ctx =>
            {
                headerSeenDownstream = ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
                return Task.CompletedTask;
            },
            _correlationContext,
            _logger);

        await middleware.InvokeAsync(context);

        Assert.False(string.IsNullOrEmpty(headerSeenDownstream));
    }

    [Fact]
    public void Header_name_is_shared_with_the_integration_layer()
    {
        // Inbound and outbound must not drift apart; both read the same constant.
        Assert.Equal(CorrelationHeaders.Name, CorrelationIdMiddleware.HeaderName);
    }

    private Task InvokeAsync(HttpContext context)
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, _correlationContext, _logger);
        return middleware.InvokeAsync(context);
    }
}

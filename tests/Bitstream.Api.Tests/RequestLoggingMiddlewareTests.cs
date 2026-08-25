using Bitstream.Hosting.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bitstream.Api.Tests;

/// <summary>
/// One structured entry per request, at a level that matches what happened. The levels are not
/// cosmetic: TR-SEC-19 and TR-INT-23 require rejected access to be recorded as a security
/// event, and TR-NFR-02 and TR-INT-30 are stated as percentiles, which needs a duration on
/// every entry.
/// </summary>
public sealed class RequestLoggingMiddlewareTests
{
    private readonly TestLogger<RequestLoggingMiddleware> _logger = new();

    [Fact]
    public async Task Logs_one_entry_per_request_with_method_route_status_and_duration()
    {
        var context = CreateContext("GET", "/Operations/reconciliation");

        await InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("GET", entry.Field("RequestMethod"));
        Assert.Equal("/Operations/reconciliation", entry.Field("RequestPath"));
        Assert.Equal(200, entry.Field("StatusCode"));
        Assert.NotNull(entry.Field("ElapsedMilliseconds"));
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    public async Task Logs_rejected_access_at_warning(int statusCode)
    {
        var context = CreateContext("POST", "/api/v1/tickets/ISP_1024/events");

        await InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public async Task Logs_a_server_error_at_error()
    {
        var context = CreateContext("POST", "/api/v1/tickets/ISP_1024/events");

        await InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });

        Assert.Equal(LogLevel.Error, Assert.Single(_logger.Entries).Level);
    }

    [Fact]
    public async Task Logs_a_client_error_at_information()
    {
        // A 400 from CRM is a contract problem for CRM to fix, not a portal fault.
        var context = CreateContext("POST", "/api/v1/tickets/ISP_1024/events");

        await InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });

        Assert.Equal(LogLevel.Information, Assert.Single(_logger.Entries).Level);
    }

    [Fact]
    public async Task Drops_successful_health_probes_to_debug()
    {
        // Probes run every few seconds; at Information they bury everything else.
        var context = CreateContext("GET", "/health/ready");

        await InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        Assert.Equal(LogLevel.Debug, Assert.Single(_logger.Entries).Level);
    }

    [Fact]
    public async Task Still_logs_a_failing_health_probe()
    {
        // The failing case is the one anyone actually looks for.
        var context = CreateContext("GET", "/health/ready");

        await InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        });

        Assert.Equal(LogLevel.Error, Assert.Single(_logger.Entries).Level);
    }

    [Fact]
    public async Task Logs_and_rethrows_an_unhandled_exception()
    {
        var context = CreateContext("POST", "/api/v1/tickets/ISP_1024/events");
        var thrown = new InvalidOperationException("adapter exploded");

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(context, _ => throw thrown));

        Assert.Same(thrown, caught);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(thrown, entry.Exception);
        Assert.NotNull(entry.Field("ElapsedMilliseconds"));
    }

    [Fact]
    public async Task Does_not_log_the_query_string_or_request_headers()
    {
        // Either can carry personal data (TR-NFR-20) or a token (TR-SEC-28).
        var context = CreateContext("GET", "/Operations/integration/dead-letter");
        context.Request.QueryString = new QueryString("?email=isp.contact%40example.com");
        context.Request.Headers.Authorization = "Bearer super-secret-token";

        await InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(_logger.Entries);
        Assert.DoesNotContain("example.com", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-token", entry.Message, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    private Task InvokeAsync(HttpContext context, RequestDelegate next) =>
        new RequestLoggingMiddleware(next, _logger).InvokeAsync(context);
}

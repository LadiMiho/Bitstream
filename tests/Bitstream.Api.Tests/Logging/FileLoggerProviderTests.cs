using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Hosting.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bitstream.Api.Tests.Logging;

/// <summary>The <c>Logging:File</c> text log, and browser errors reaching the server log through <c>POST /ClientLog</c>.</summary>
public sealed class FileLoggerProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 14, 3, 512, TimeSpan.FromHours(2));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bitstream-log-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileLoggerProvider CreateProvider(LogLevel minimum = LogLevel.Information, int retainedDays = 14) =>
        new(new FileLoggerOptions { Directory = _directory, FileNamePrefix = "bitstream-web", MinimumLevel = minimum, RetainedDays = retainedDays },
            contentRoot: "/unused", clock: () => Now);

    private string ReadToday() => File.ReadAllText(Path.Combine(_directory, "bitstream-web-20261012.log"));

    [Fact]
    public void Writes_one_line_per_entry_with_level_category_correlation_and_the_exception()
    {
        using (var provider = CreateProvider())
        {
            var scopes = new LoggerExternalScopeProvider();
            provider.SetScopeProvider(scopes);
            var logger = provider.CreateLogger("Bitstream.Web.Controllers.AuthController");

            using (scopes.Push(new Dictionary<string, object> { ["CorrelationId"] = "corr-123" }))
            {
                logger.LogError(new InvalidOperationException("Database unreachable"), "Login failed for {UserId}", 42);
            }

            logger.LogInformation("Second entry");
        }

        var text = ReadToday();

        Assert.Contains("2026-10-12 09:14:03.512 +02:00 [ERR] Bitstream.Web.Controllers.AuthController [corr=corr-123] Login failed for 42", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: Database unreachable", text, StringComparison.Ordinal);
        Assert.Contains("[INF] Bitstream.Web.Controllers.AuthController Second entry", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_not_written()
    {
        using (var provider = CreateProvider(LogLevel.Warning))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("not written");
            logger.LogWarning("written");
        }

        var text = ReadToday();
        Assert.DoesNotContain("not written", text, StringComparison.Ordinal);
        Assert.Contains("[WRN] Test written", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_older_than_the_retention_are_deleted()
    {
        Directory.CreateDirectory(_directory);
        var old = Path.Combine(_directory, "bitstream-web-20260901.log");
        var recent = Path.Combine(_directory, "bitstream-web-20261010.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");

        using (var provider = CreateProvider(retainedDays: 14))
        {
            provider.CreateLogger("Test").LogInformation("today");
        }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task A_browser_error_report_is_logged_as_a_warning_on_one_line()
    {
        var captured = new CapturingLoggerProvider();
        await using var factory = new IdentityApiFactory();
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(captured)));
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/ClientLog", UriKind.Relative),
            new { page = "login", message = "Could not reach the server: Failed to fetch", status = 0, url = "/Login", detail = "line1\nline2" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var entry = Assert.Single(captured.Entries, e => e.Category.EndsWith("ClientLogController", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Client error on login: Could not reach the server: Failed to fetch (HTTP 0, /Login) line1 line2", entry.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((category, logLevel, formatter(state, exception)));
        }
    }
}

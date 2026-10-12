using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Bitstream.Hosting.Logging;

/// <summary>
/// Writes log entries to <c>{Directory}/{FileNamePrefix}-yyyyMMdd.log</c>, one line per entry
/// with the exception (type, message, stack trace) below it. Entries are queued and written by
/// one background task, so a request never waits on the disk; a full queue drops the newest
/// entry rather than block. If the file cannot be written, file logging stops and says so once
/// on standard error — the application itself is never affected.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Channel<(DateTimeOffset At, string Text)> _queue =
        Channel.CreateBounded<(DateTimeOffset, string)>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _retainedDays;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Task _writer;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private volatile bool _failed;

    public FileLoggerProvider(FileLoggerOptions options, string contentRoot, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        MinimumLevel = options.MinimumLevel;
        _directory = Path.GetFullPath(Path.IsPathRooted(options.Directory) ? options.Directory : Path.Combine(contentRoot, options.Directory));
        _prefix = string.IsNullOrWhiteSpace(options.FileNamePrefix) ? "bitstream" : options.FileNamePrefix;
        _retainedDays = options.RetainedDays;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _writer = Task.Run(WriteLoopAsync);
    }

    public LogLevel MinimumLevel { get; }

    /// <summary>The file today's entries go to.</summary>
    public string CurrentFilePath => PathFor(_clock());

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose()
    {
        _queue.Writer.TryComplete();

        try
        {
            // Let the last entries (often the reason the host is stopping) reach the file.
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Already reported by the write loop.
        }
    }

    internal void Enqueue(DateTimeOffset at, string text)
    {
        if (!_failed)
        {
            _queue.Writer.TryWrite((at, text));
        }
    }

    internal IExternalScopeProvider ScopeProvider => _scopeProvider;

    internal DateTimeOffset Now => _clock();

    private string PathFor(DateTimeOffset at) =>
        Path.Combine(_directory, $"{_prefix}-{at.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    private async Task WriteLoopAsync()
    {
        string? openPath = null;
        StreamWriter? writer = null;

        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var entry))
                {
                    var path = PathFor(entry.At);

                    if (path != openPath)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }

                        System.IO.Directory.CreateDirectory(_directory);
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                        openPath = path;
                        DeleteOldFiles(entry.At);
                    }

                    await writer!.WriteAsync(entry.Text).ConfigureAwait(false);
                }

                await writer!.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _failed = true;
            await Console.Error.WriteLineAsync($"File logging to '{_directory}' stopped: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void DeleteOldFiles(DateTimeOffset now)
    {
        if (_retainedDays <= 0)
        {
            return;
        }

        var cutoff = now.Date.AddDays(-_retainedDays);

        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[(_prefix.Length + 1)..];

            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // In use or already gone: try again tomorrow.
                }
            }
        }
    }
}

/// <summary>One category's logger; formats the entry and hands it to <see cref="FileLoggerProvider"/>.</summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _provider.ScopeProvider.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _provider.MinimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);

        var now = _provider.Now;
        var builder = new StringBuilder(256);
        builder.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
            .Append(_category);

        // The correlation ID (CorrelationIdMiddleware's scope) ties the lines of one request together.
        _provider.ScopeProvider.ForEachScope(
            static (scope, sb) =>
            {
                var value = scope switch
                {
                    IEnumerable<KeyValuePair<string, object>> pairs => pairs.FirstOrDefault(p => IsCorrelation(p.Key)).Value,
                    _ => null
                };

                if (value is not null)
                {
                    sb.Append(" [corr=").Append(value).Append(']');
                }
            },
            builder);

        builder.Append(' ').Append(formatter(state, exception)).AppendLine();

        if (exception is not null)
        {
            builder.Append(exception).AppendLine();
        }

        _provider.Enqueue(now, builder.ToString());
    }

    private static bool IsCorrelation(string key) => string.Equals(key, "CorrelationId", StringComparison.OrdinalIgnoreCase);

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}

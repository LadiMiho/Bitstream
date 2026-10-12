namespace Bitstream.Hosting.Logging;

/// <summary>
/// <c>Logging:File</c>: a plain-text log file per day, next to the console output, so a fault
/// can be read after the fact even where the console is not visible (IIS, a service).
/// </summary>
public sealed class FileLoggerOptions
{
    public const string SectionName = "Logging:File";

    public bool Enabled { get; set; } = true;

    /// <summary>Folder for the files; a relative path is resolved against the host's content root.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>File name start, e.g. <c>bitstream-web</c> gives <c>bitstream-web-20261012.log</c>.</summary>
    public string? FileNamePrefix { get; set; }

    /// <summary>Lowest level written to the file; the usual <c>Logging:LogLevel</c> rules apply as well.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Files older than this many days are deleted. 0 keeps everything.</summary>
    public int RetainedDays { get; set; } = 14;
}

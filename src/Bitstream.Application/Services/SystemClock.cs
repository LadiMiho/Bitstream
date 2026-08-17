using Bitstream.Application.Abstractions.Time;

namespace Bitstream.Application.Services;

/// <summary>Wall-clock <see cref="IClock"/>. All timestamps are UTC with the offset preserved (TR-DAT-08).</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

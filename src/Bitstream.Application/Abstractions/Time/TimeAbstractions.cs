namespace Bitstream.Application.Abstractions.Time;

/// <summary>
/// Clock abstraction. All timestamps are UTC with the offset preserved (TR-DAT-08).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Working-day calendar used by the auto-confirmation schedule (TR-PAS-21a).
/// The holiday list is maintainable configuration, not code.
/// </summary>
public interface IWorkingDayCalendar
{
    bool IsWorkingDay(DateOnly date);

    /// <summary>Returns the instant <paramref name="workingDays"/> working days after <paramref name="from"/>.</summary>
    DateTimeOffset AddWorkingDays(DateTimeOffset from, int workingDays);

    /// <summary>Working days elapsed between two instants, used for the elapsed period reported to CRM (TR-PAS-21d).</summary>
    int WorkingDaysBetween(DateTimeOffset from, DateTimeOffset to);
}

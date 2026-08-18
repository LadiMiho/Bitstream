using Bitstream.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>
/// Working-day arithmetic backing the auto-confirmation clock (TR-PAS-21a). "Day 2" and "day 4"
/// in TRD 6.5 are working days, not calendar days, and must skip the configured weekend and
/// public holiday list (<see cref="WorkingCalendarOptions"/>) — this is the one place that math
/// happens, so the reminder, auto-confirm and challenge-window deadlines cannot drift apart.
/// </summary>
public interface IWorkingDayCalculator
{
    /// <summary>
    /// The timestamp <paramref name="workingDays"/> working days after <paramref name="start"/>,
    /// counting forward from the day after <paramref name="start"/>'s calendar date. Same
    /// time-of-day as <paramref name="start"/>, in the configured time zone.
    /// </summary>
    DateTimeOffset AddWorkingDays(DateTimeOffset start, int workingDays);
}

public sealed class WorkingDayCalculator : IWorkingDayCalculator
{
    private readonly IOptionsMonitor<WorkingCalendarOptions> _options;

    public WorkingDayCalculator(IOptionsMonitor<WorkingCalendarOptions> options) => _options = options;

    public DateTimeOffset AddWorkingDays(DateTimeOffset start, int workingDays)
    {
        var options = _options.CurrentValue;
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(start, timeZone);

        var date = DateOnly.FromDateTime(local.DateTime);
        var remaining = workingDays;

        while (remaining > 0)
        {
            date = date.AddDays(1);

            if (IsWorkingDay(date, options))
            {
                remaining--;
            }
        }

        var result = new DateTime(date.Year, date.Month, date.Day, local.Hour, local.Minute, local.Second, local.Millisecond, DateTimeKind.Unspecified);
        var offset = timeZone.GetUtcOffset(result);

        return new DateTimeOffset(result, offset);
    }

    private static bool IsWorkingDay(DateOnly date, WorkingCalendarOptions options) =>
        options.WorkingDays.Contains(date.DayOfWeek) && !options.PublicHolidays.Contains(date);
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Configuration;

/// <summary>
/// Validation of the platform options.
/// <para>
/// Two principles. First, a value that is <em>absent</em> is tolerated so that a developer or a
/// test host starts without a full production configuration; a value that is <em>present and
/// wrong</em> is a hard failure. Second, requirements that are easy to violate by
/// configuration — TR-PAS-21b in particular — are encoded here rather than left to review.
/// </para>
/// <para>
/// Absent-but-required-in-production values are caught separately by the readiness report,
/// so that a missing CRM endpoint fails a deployment rather than a developer's F5.
/// </para>
/// </summary>
public sealed partial class IdentifierOptionsValidator : IValidateOptions<IdentifierOptions>
{
    [GeneratedRegex("^[A-Z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    public ValidateOptionsResult Validate(string? name, IdentifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidatePrefix(nameof(options.ActivationRequestPrefix), options.ActivationRequestPrefix, failures);
        ValidatePrefix(nameof(options.ComplaintTicketPrefix), options.ComplaintTicketPrefix, failures);
        ValidatePrefix(nameof(options.ServiceChangeRequestPrefix), options.ServiceChangeRequestPrefix, failures);

        // TR-DAT-06: complaint tickets use a separate, distinguishable series.
        if (!string.IsNullOrEmpty(options.ActivationRequestPrefix) &&
            string.Equals(options.ActivationRequestPrefix, options.ComplaintTicketPrefix, StringComparison.Ordinal))
        {
            failures.Add(
                "Identifiers: ComplaintTicketPrefix must differ from ActivationRequestPrefix so that the " +
                "two series are distinguishable (TR-DAT-06).");
        }

        if (string.IsNullOrWhiteSpace(options.Pattern))
        {
            failures.Add("Identifiers:Pattern must not be empty (TR-DAT-02d).");
        }
        else if (!IsValidRegex(options.Pattern))
        {
            failures.Add($"Identifiers:Pattern is not a valid regular expression: '{options.Pattern}'.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePrefix(string key, string value, List<string> failures)
    {
        if (string.IsNullOrEmpty(value))
        {
            // Unset is tolerated: the value is TRD 11.4 open item 2 and is supplied per
            // environment. Attempting to issue an identifier without it fails loudly.
            return;
        }

        if (!PrefixPattern().IsMatch(value))
        {
            failures.Add(
                $"Identifiers:{key} must be uppercase letters only to satisfy ^[A-Z]+_[0-9]+$ " +
                $"(TR-DAT-02d). Configured value: '{value}'.");
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = Regex.Match(string.Empty, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Validates the reference catalogues.</summary>
public sealed class CatalogueOptionsValidator : IValidateOptions<CatalogueOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        var duplicatePackages = options.Packages
            .GroupBy(package => package.Code, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicatePackages.Count > 0)
        {
            failures.Add($"Catalogues:Packages contains duplicate codes: {string.Join(", ", duplicatePackages)}.");
        }

        foreach (var package in options.Packages.Where(p => string.IsNullOrWhiteSpace(p.Code)))
        {
            failures.Add($"Catalogues:Packages contains an entry with no Code (Name: '{package.Name}').");
        }

        // TR-ACT-04: the form defaults to a classification, which therefore has to exist.
        if (options.Classifications.Count > 0 &&
            !options.Classifications.Contains(options.DefaultClassification, StringComparer.Ordinal))
        {
            failures.Add(
                $"Catalogues:DefaultClassification '{options.DefaultClassification}' is not present in " +
                "Catalogues:Classifications (TR-ACT-04).");
        }

        if (options.ContractDurationsMonths.Any(months => months <= 0))
        {
            failures.Add("Catalogues:ContractDurationsMonths must contain positive values only.");
        }

        foreach (var category in options.ComplaintCategories)
        {
            if (string.IsNullOrWhiteSpace(category.L1) || string.IsNullOrWhiteSpace(category.L2) || string.IsNullOrWhiteSpace(category.L3))
            {
                failures.Add("Catalogues:ComplaintCategories entries must all have L1, L2 and L3 set (TR-PAS-08).");
                break;
            }
        }

        var duplicateCategories = options.ComplaintCategories
            .GroupBy(c => (c.L1, c.L2, c.L3))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.L1}/{group.Key.L2}/{group.Key.L3}")
            .ToList();

        if (duplicateCategories.Count > 0)
        {
            failures.Add($"Catalogues:ComplaintCategories contains duplicate entries: {string.Join(", ", duplicateCategories)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates the closure timings, including the rule that reminders cannot be switched off.</summary>
public sealed class TicketClosureOptionsValidator : IValidateOptions<TicketClosureOptions>
{
    public ValidateOptionsResult Validate(string? name, TicketClosureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.AutoConfirmAfterWorkingDays <= 0)
        {
            failures.Add("TicketClosure:AutoConfirmAfterWorkingDays must be greater than zero (TR-PAS-21a).");
        }

        if (options.ChallengeWindowCalendarDays < 0)
        {
            failures.Add("TicketClosure:ChallengeWindowCalendarDays must not be negative (TR-PAS-21f).");
        }

        if (options.SweepInterval <= TimeSpan.Zero)
        {
            failures.Add("TicketClosure:SweepInterval must be greater than zero.");
        }

        if (options.AutoConfirmationEnabled)
        {
            // TR-PAS-21b: disabling reminders entirely must not be possible while
            // auto-confirmation is enabled. No ISP is closed out silently.
            if (options.ReminderAfterWorkingDays.Count == 0)
            {
                failures.Add(
                    "TicketClosure:ReminderAfterWorkingDays must contain at least one reminder while " +
                    "AutoConfirmationEnabled is true. Disabling reminders entirely is not permitted " +
                    "(TR-PAS-21b).");
            }

            foreach (var reminder in options.ReminderAfterWorkingDays)
            {
                if (reminder <= 0)
                {
                    failures.Add($"TicketClosure:ReminderAfterWorkingDays contains a non-positive offset ({reminder}).");
                }
                else if (reminder >= options.AutoConfirmAfterWorkingDays)
                {
                    failures.Add(
                        $"TicketClosure:ReminderAfterWorkingDays contains {reminder}, which is not before " +
                        $"AutoConfirmAfterWorkingDays ({options.AutoConfirmAfterWorkingDays}). A reminder sent " +
                        "at or after the deadline warns nobody (TR-PAS-21b).");
                }
            }

            var ordered = options.ReminderAfterWorkingDays.OrderBy(day => day).ToList();

            if (!ordered.SequenceEqual(options.ReminderAfterWorkingDays))
            {
                failures.Add("TicketClosure:ReminderAfterWorkingDays must be listed in ascending order.");
            }

            if (ordered.Distinct().Count() != ordered.Count)
            {
                failures.Add("TicketClosure:ReminderAfterWorkingDays must not contain duplicates.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates the working-day calendar.</summary>
public sealed class WorkingCalendarOptionsValidator : IValidateOptions<WorkingCalendarOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkingCalendarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.WorkingDays.Count == 0)
        {
            failures.Add("WorkingCalendar:WorkingDays must contain at least one day, or no deadline can ever elapse.");
        }

        if (string.IsNullOrWhiteSpace(options.TimeZoneId))
        {
            failures.Add("WorkingCalendar:TimeZoneId must be set (TR-DAT-08).");
        }
        else
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                failures.Add($"WorkingCalendar:TimeZoneId '{options.TimeZoneId}' is not a time zone on this host.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

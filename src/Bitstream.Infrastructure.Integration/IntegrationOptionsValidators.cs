using Bitstream.Infrastructure.Integration.Bi;
using Bitstream.Infrastructure.Integration.Crm;
using Bitstream.Infrastructure.Integration.Mail;
using Bitstream.Infrastructure.Integration.Sap;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Integration;

/// <summary>
/// Validation of the adapter options.
/// <para>
/// An unconfigured endpoint is tolerated — the CRM and BI contracts are outstanding (TRD 11.4
/// open item 1, TRD 11.2), and a developer must be able to start the host without them. What is
/// not tolerated is a value that is present and wrong, or a combination that would quietly
/// weaken a control: a retry budget that cannot honour TR-INT-04, or a non-production redirect
/// switched on with no mailbox to redirect to (TR-NTF-07).
/// </para>
/// </summary>
public sealed class CrmOptionsValidator : IValidateOptions<CrmOptions>
{
    public ValidateOptionsResult Validate(string? name, CrmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.BaseAddress is not null && !options.BaseAddress.IsAbsoluteUri)
        {
            failures.Add($"Integration:Crm:BaseAddress must be an absolute URI. Configured: '{options.BaseAddress}'.");
        }

        // TR-SEC-26: all system-to-system traffic uses TLS.
        if (options.BaseAddress is { IsAbsoluteUri: true, Scheme: not "https" })
        {
            failures.Add(
                $"Integration:Crm:BaseAddress must use https. TLS 1.2 or higher is required for all " +
                $"traffic (TR-SEC-26). Configured scheme: '{options.BaseAddress.Scheme}'.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            failures.Add("Integration:Crm:Timeout must be greater than zero (TR-INT-08).");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add("Integration:Crm:MaxAttempts must be at least 1 (TR-INT-04).");
        }

        if (options.RetryWindow <= TimeSpan.Zero)
        {
            failures.Add("Integration:Crm:RetryWindow must be greater than zero (TR-INT-04).");
        }

        // A retry budget that cannot fit its own attempts inside its window is a configuration
        // that looks like resilience and is not.
        if (options.MaxAttempts > 1 && options.RetryWindow < options.Timeout)
        {
            failures.Add(
                $"Integration:Crm:RetryWindow ({options.RetryWindow}) is shorter than a single Timeout " +
                $"({options.Timeout}); no retry could complete inside the window (TR-INT-04).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates the BI adapter options.</summary>
public sealed class BiOptionsValidator : IValidateOptions<BiOptions>
{
    public ValidateOptionsResult Validate(string? name, BiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.BaseAddress is not null && !options.BaseAddress.IsAbsoluteUri)
        {
            failures.Add($"Integration:Bi:BaseAddress must be an absolute URI. Configured: '{options.BaseAddress}'.");
        }

        if (options.BaseAddress is { IsAbsoluteUri: true, Scheme: not "https" })
        {
            failures.Add("Integration:Bi:BaseAddress must use https (TR-SEC-26).");
        }

        if (options.SyncInterval <= TimeSpan.Zero)
        {
            failures.Add("Integration:Bi:SyncInterval must be greater than zero (TR-PAS-03).");
        }

        if (options.PageSize < 1)
        {
            failures.Add("Integration:Bi:PageSize must be at least 1 (TR-NFR-05).");
        }

        if (options.FailureAlertThreshold < 1)
        {
            failures.Add("Integration:Bi:FailureAlertThreshold must be at least 1 (TR-PAS-07).");
        }

        // TR-PAS-02: the technology filter is configurable, but an empty list would show the ISP
        // nothing at all, which is a silent outage rather than a filter.
        if (options.IncludedTechnologies.Count == 0)
        {
            failures.Add(
                "Integration:Bi:IncludedTechnologies must list at least one technology; an empty list " +
                "hides every line from every ISP (TR-PAS-02).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates the SAP adapter options.</summary>
public sealed class SapOptionsValidator : IValidateOptions<SapOptions>
{
    public ValidateOptionsResult Validate(string? name, SapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.BaseAddress is not null && !options.BaseAddress.IsAbsoluteUri)
        {
            failures.Add($"Integration:Sap:BaseAddress must be an absolute URI. Configured: '{options.BaseAddress}'.");
        }

        // Enabling the adapter before the population point is decided (TRD 11.4 open item 5)
        // would mean calling an interface whose direction is not yet agreed.
        if (options.Enabled && options.BaseAddress is null)
        {
            failures.Add(
                "Integration:Sap:Enabled is true but no BaseAddress is configured. The financial code " +
                "population point is TRD 11.4 open item 5 and the adapter is not implemented.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates the SMTP adapter options.</summary>
public sealed class SmtpOptionsValidator : IValidateOptions<SmtpOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"Integration:Smtp:Port must be between 1 and 65535. Configured: {options.Port}.");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add("Integration:Smtp:MaxAttempts must be at least 1 (TR-NTF-04).");
        }

        if (!string.IsNullOrWhiteSpace(options.FromAddress) && !options.FromAddress.Contains('@', StringComparison.Ordinal))
        {
            failures.Add($"Integration:Smtp:FromAddress is not an email address: '{options.FromAddress}'.");
        }

        // TR-NTF-07: redirect mode with no mailbox would drop every message on the floor while
        // appearing to be configured.
        if (options.RedirectAllMail && string.IsNullOrWhiteSpace(options.RedirectMailbox))
        {
            failures.Add(
                "Integration:Smtp:RedirectAllMail is true but RedirectMailbox is empty. Test mode must " +
                "redirect to a controlled mailbox, not to nowhere (TR-NTF-07).");
        }

        foreach (var (groupName, members) in options.DistributionGroups)
        {
            foreach (var member in members.Where(m => !m.Contains('@', StringComparison.Ordinal)))
            {
                failures.Add($"Integration:Smtp:DistributionGroups:{groupName} contains a non-address entry: '{member}'.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

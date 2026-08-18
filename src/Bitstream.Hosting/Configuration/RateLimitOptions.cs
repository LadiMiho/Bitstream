using Microsoft.Extensions.Options;

namespace Bitstream.Hosting.Configuration;

/// <summary>
/// Rate-limit policies, TR-SEC-29 and TR-INT-30.
/// Limits are configuration because the workable value depends on the environment: CRM's event
/// rate in UAT is not its rate in production.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Limit applied to the CRM inbound event API (TR-INT-30).</summary>
    public RateLimitPolicyOptions CrmInbound { get; set; } = new() { PermitLimit = 200, WindowSeconds = 1 };

    /// <summary>Limit applied to administrative and operational endpoints.</summary>
    public RateLimitPolicyOptions Administration { get; set; } = new() { PermitLimit = 60, WindowSeconds = 60 };

    /// <summary>
    /// Limit applied to <c>/api/v1/auth/*</c> — tighter than <see cref="Administration"/> on
    /// purpose (TR-SEC-29): this is exactly where a credential-stuffing or lockout-triggering
    /// attempt would land. The account-lockout threshold (TR-SEC-06) still applies per account
    /// regardless of this limit, which applies per caller.
    /// </summary>
    public RateLimitPolicyOptions Authentication { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };
}

/// <summary>A single fixed-window policy.</summary>
public sealed class RateLimitPolicyOptions
{
    /// <summary>Requests permitted inside the window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>Window length in seconds.</summary>
    public int WindowSeconds { get; set; }
}

/// <summary>Validates the rate-limit policies.</summary>
public sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        Check(nameof(options.CrmInbound), options.CrmInbound, failures);
        Check(nameof(options.Administration), options.Administration, failures);
        Check(nameof(options.Authentication), options.Authentication, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(string key, RateLimitPolicyOptions policy, List<string> failures)
    {
        // A zero permit limit rejects everything. That is not a rate limit, it is an outage,
        // and it is an easy thing to configure by accident.
        if (policy.PermitLimit < 1)
        {
            failures.Add($"RateLimits:{key}:PermitLimit must be at least 1; {policy.PermitLimit} rejects every request.");
        }

        if (policy.WindowSeconds < 1)
        {
            failures.Add($"RateLimits:{key}:WindowSeconds must be at least 1.");
        }
    }
}

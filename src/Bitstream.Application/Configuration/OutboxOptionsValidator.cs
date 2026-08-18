using Microsoft.Extensions.Options;

namespace Bitstream.Application.Configuration;

/// <summary>Validates <see cref="OutboxDispatcherOptions"/>.</summary>
public sealed class OutboxDispatcherOptionsValidator : IValidateOptions<OutboxDispatcherOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("Integration:OutboxDispatcher:PollInterval must be greater than zero.");
        }

        if (options.BatchSize < 1)
        {
            failures.Add("Integration:OutboxDispatcher:BatchSize must be at least 1.");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add("Integration:OutboxDispatcher:MaxAttempts must be at least 1 (TR-INT-04).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

using Microsoft.Extensions.Options;

namespace Bitstream.Hosting.Configuration;

/// <summary>
/// Resolves every registered options type once at start-up so that validation runs before the
/// first request rather than on the first request that happens to need a value.
/// <para>
/// TR-ARC-06 puts endpoints, credentials, templates and lists in configuration; the cost of
/// that is that a typo becomes a runtime problem instead of a compile error. Failing the host
/// start moves it back to deployment time, where TR-ARC-08's scripted provisioning can catch it.
/// </para>
/// </summary>
public sealed class OptionsStartupValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<Type> _optionTypes;
    private readonly ILogger<OptionsStartupValidator> _logger;

    public OptionsStartupValidator(
        IServiceProvider services,
        IEnumerable<Type> optionTypes,
        ILogger<OptionsStartupValidator> logger)
    {
        _services = services;
        _optionTypes = [.. optionTypes];
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var optionType in _optionTypes)
        {
            var accessorType = typeof(IOptions<>).MakeGenericType(optionType);
            var accessor = _services.GetService(accessorType);

            if (accessor is null)
            {
                failures.Add($"{optionType.Name} is listed for validation but is not registered.");
                continue;
            }

            try
            {
                // Reading Value is what triggers binding and every IValidateOptions registered
                // for the type.
                _ = accessorType.GetProperty(nameof(IOptions<object>.Value))!.GetValue(accessor);
            }
            catch (Exception exception) when (exception.InnerException is OptionsValidationException validation)
            {
                failures.AddRange(validation.Failures);
            }
            catch (OptionsValidationException validation)
            {
                failures.AddRange(validation.Failures);
            }
        }

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                _logger.LogCritical("Configuration error: {Failure}", failure);
            }

            throw new OptionsValidationException(
                "Bitstream configuration",
                typeof(OptionsStartupValidator),
                failures);
        }

        _logger.LogInformation("Configuration validated: {OptionTypeCount} option sets.", _optionTypes.Count);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

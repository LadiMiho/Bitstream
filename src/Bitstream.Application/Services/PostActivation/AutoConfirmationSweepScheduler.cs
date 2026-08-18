using Bitstream.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>Runs <see cref="ITicketClosureService.RunAutoConfirmationSweepAsync"/> on <see cref="TicketClosureOptions.SweepInterval"/> (TR-PAS-21).</summary>
public sealed class AutoConfirmationSweepScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<TicketClosureOptions> _options;
    private readonly ILogger<AutoConfirmationSweepScheduler> _logger;

    public AutoConfirmationSweepScheduler(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<TicketClosureOptions> options,
        ILogger<AutoConfirmationSweepScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var closureService = scope.ServiceProvider.GetRequiredService<ITicketClosureService>();
                await closureService.RunAutoConfirmationSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Auto-confirmation sweep failed; will retry on the next interval.");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }
    }
}

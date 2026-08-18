using Bitstream.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>Runs <see cref="IActiveLineSyncService.SynchroniseAsync"/> on <see cref="ActiveLineSyncOptions.SyncInterval"/> (TR-PAS-03).</summary>
public sealed class ActiveLineSyncScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ActiveLineSyncOptions> _options;
    private readonly ILogger<ActiveLineSyncScheduler> _logger;

    public ActiveLineSyncScheduler(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ActiveLineSyncOptions> options,
        ILogger<ActiveLineSyncScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.CurrentValue.ScheduledSyncEnabled)
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var syncService = scope.ServiceProvider.GetRequiredService<IActiveLineSyncService>();
                    await syncService.SynchroniseAsync(fullReload: false, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Scheduled active-lines synchronisation failed; will retry on the next interval.");
                }
            }

            try
            {
                await Task.Delay(_options.CurrentValue.SyncInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }
    }
}

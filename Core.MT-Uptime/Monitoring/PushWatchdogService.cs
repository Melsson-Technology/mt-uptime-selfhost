using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Drives overdue detection for push monitors: on a short fixed tick it asks the
/// <see cref="PushMonitorManager"/> to mark any monitor whose next ping has passed its
/// deadline (period + grace) as down. The per-monitor deadline lives in the manager;
/// this only decides how promptly a lapse is noticed.
/// </summary>
public sealed class PushWatchdogService(PushMonitorManager manager, ILogger<PushWatchdogService> log)
    : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the scheduler register push monitors first, and don't fire the instant the app boots.
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { manager.CheckOverdue(); }
            catch (Exception ex) { log.LogError(ex, "Push watchdog tick failed"); }

            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }
}

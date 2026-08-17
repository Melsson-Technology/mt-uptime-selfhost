using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Orchestrates the engine: one <see cref="MonitorRunner"/> per enabled monitor, a shared concurrency
/// gate, and live <see cref="ReloadAsync"/>/<see cref="RemoveAsync"/> so UI edits reconfigure monitoring
/// without a restart.
/// </summary>
public sealed class MonitorSchedulerService(
    IDbContextFactory<AppDbContext> factory,
    IEnumerable<IMonitorChecker> checkers,
    HeartbeatWriter writer,
    NotificationDispatcher dispatcher,
    MonitorStateService state,
    PushMonitorManager push,
    IOptions<EngineOptions> options,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly EngineOptions _options = options.Value;
    private readonly ILogger<MonitorSchedulerService> _log = loggerFactory.CreateLogger<MonitorSchedulerService>();
    private readonly ConcurrentDictionary<int, MonitorRunner> _runners = new();
    private readonly SemaphoreSlim _mutation = new(1, 1); // serialize startup vs reload/remove

    private IReadOnlyDictionary<MonitorType, IMonitorChecker> _checkers = null!;
    private SemaphoreSlim? _gate;
    private CancellationToken _appStopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appStopping = stoppingToken;
        _checkers = checkers.ToDictionary(c => c.Type);
        var max = _options.ResolveMaxConcurrency();
        _gate = new SemaphoreSlim(max, max);
        _log.LogInformation("Monitoring engine starting (max concurrency {Max})", max);

        await _mutation.WaitAsync(stoppingToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(stoppingToken);
            var monitors = await db.Monitors.AsNoTracking().ToListAsync(stoppingToken);
            foreach (var m in monitors)
            {
                state.Seed(m);
                if (m.Enabled) Activate(m);
            }
        }
        finally { _mutation.Release(); }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        _log.LogInformation("Monitoring engine stopping ({Count} runners)", _runners.Count);
        await Task.WhenAll(_runners.Values.Select(r => r.StopAsync()));
        _runners.Clear();
    }

    /// <summary>Reload a monitor after it was created or edited: restart its runner from the current DB row.</summary>
    public async Task ReloadAsync(int monitorId)
    {
        if (_gate is null) return; // not started yet; the startup scan will pick it up

        await _mutation.WaitAsync();
        try
        {
            if (_runners.TryRemove(monitorId, out var existing)) await existing.StopAsync();
            push.Unregister(monitorId); // may have been (or may become) a push monitor

            await using var db = await factory.CreateDbContextAsync();
            var m = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == monitorId);
            if (m is null) { state.Remove(monitorId); return; }

            state.Seed(m);
            if (m.Enabled) Activate(m);
        }
        finally { _mutation.Release(); }
    }

    public async Task RemoveAsync(int monitorId)
    {
        await _mutation.WaitAsync();
        try
        {
            if (_runners.TryRemove(monitorId, out var existing)) await existing.StopAsync();
            push.Unregister(monitorId);
            state.Remove(monitorId);
        }
        finally { _mutation.Release(); }
    }

    // Route a monitor to its execution model: passive push monitors register with the manager;
    // everything else gets an active check runner.
    private void Activate(Monitor m)
    {
        if (m.Type == MonitorType.Push) push.Register(m);
        else StartRunner(m);
    }

    private void StartRunner(Monitor m)
    {
        var runner = new MonitorRunner(m, _checkers, _gate!, writer, dispatcher, state,
            loggerFactory.CreateLogger<MonitorRunner>());
        _runners[m.Id] = runner;
        runner.Start(_appStopping);
    }
}

using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Runs one monitor on its own interval loop. Idle between ticks it costs almost nothing (a suspended
/// state machine + a timer), so thousands of monitors scale fine; the shared semaphore is what bounds
/// concurrent work. Started and stopped by <see cref="MonitorSchedulerService"/>.
/// </summary>
public sealed class MonitorRunner
{
    private readonly Monitor _monitor;
    private readonly MonitorContext _ctx;
    private readonly IReadOnlyDictionary<MonitorType, IMonitorChecker> _checkers;
    private readonly SemaphoreSlim _gate;
    private readonly HeartbeatWriter _writer;
    private readonly NotificationDispatcher _dispatcher;
    private readonly MonitorStateService _state;
    private readonly ILogger _log;
    private readonly MonitorStateMachine _machine;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly int? _slowThresholdMs;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public MonitorRunner(
        Monitor monitor,
        IReadOnlyDictionary<MonitorType, IMonitorChecker> checkers,
        SemaphoreSlim gate,
        HeartbeatWriter writer,
        NotificationDispatcher dispatcher,
        MonitorStateService state,
        ILogger log)
    {
        _monitor = monitor;
        _checkers = checkers;
        _gate = gate;
        _writer = writer;
        _dispatcher = dispatcher;
        _state = state;
        _log = log;
        _interval = MonitorCadence.ResolveInterval(monitor.IntervalSeconds);
        _timeout = MonitorCadence.ResolveTimeout(monitor.TimeoutSeconds, _interval);
        if (monitor.TimeoutSeconds >= (int)_interval.TotalSeconds)
        {
            log.LogWarning(
                "Monitor {MonitorId} '{Name}': timeout {Configured}s is not shorter than its {Interval}s " +
                "interval; clamped to {Applied}s so slow checks cannot run back-to-back.",
                monitor.Id, monitor.Name, monitor.TimeoutSeconds, (int)_interval.TotalSeconds, (int)_timeout.TotalSeconds);
        }
        _slowThresholdMs = monitor.SlowThresholdMs;

        _ctx = new MonitorContext(monitor.Id, monitor.Name, monitor.Type, _timeout, monitor.ConfigJson);
        _machine = new MonitorStateMachine(monitor.RetryCount, monitor.UpsideDown, monitor.ResendEveryN,
            monitor.CurrentStatus, monitor.DegradedAfterChecks);
    }

    public void Start(CancellationToken appStopping)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(appStopping, _cts.Token);
        _loop = RunLoopAsync(linked.Token);
    }

    public async Task StopAsync()
    {
        try { await _cts.CancelAsync(); } catch { /* already disposed */ }
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { } catch { /* logged in loop */ }
        }
        _cts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Stagger startup so many monitors don't all fire at t=0.
        var jitterMs = Random.Shared.Next(0, (int)Math.Min(_interval.TotalMilliseconds, 15_000));
        try { await Task.Delay(jitterMs, ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(ct);
            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (!_checkers.TryGetValue(_monitor.Type, out var checker))
        {
            _log.LogWarning("No checker registered for monitor type {Type} (monitor {MonitorId})", _monitor.Type, _monitor.Id);
            return;
        }

        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            CheckResult result;
            try
            {
                result = await checker.CheckAsync(_ctx, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return; // shutting down — don't record a phantom failure
                result = CheckResult.Down("Timeout", _timeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                result = CheckResult.Down(ex.Message);
            }

            Process(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Process(CheckResult result)
    {
        var now = DateTime.UtcNow;

        var slow = MonitorCadence.IsSlow(result.ResponseTimeMs, _slowThresholdMs);
        var d = _machine.Evaluate(result.Status, result.Hard, slow);

        _writer.Enqueue(new CheckOutcome(
            _monitor.Id, now, d.HeartbeatStatus, result.ResponseTimeMs, result.StatusCode, result.Message,
            d.Important, d.Attempt, result.CertExpiresAt, d.EventAction, d.PreviousConfirmed, d.NewConfirmed));

        _state.ApplyResult(_monitor.Id, d.HeartbeatStatus, now, result.ResponseTimeMs, result.Message, result.CertExpiresAt);

        if (d.Notify != NotifyKind.None)
        {
            _dispatcher.Enqueue(new NotificationEvent(
                _monitor.Id, _monitor.Name, d.NewConfirmed, d.PreviousConfirmed, now, result.Message, result.ResponseTimeMs, d.Notify));
        }
    }
}

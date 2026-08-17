using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Owns the passive (push / heartbeat) monitors. Instead of an active <see cref="MonitorRunner"/> loop,
/// each push monitor has a token; the monitored job calls <c>/ping/{token}</c> on a schedule. A ping
/// records an "up" beat and recovers the monitor; the <see cref="PushWatchdogService"/> marks it down
/// when a ping is overdue. Decisions flow through the same state machine + single-writer/notification
/// seams a runner uses, so events and alerts behave identically.
/// Registered as a singleton; the scheduler registers/unregisters monitors, the endpoint feeds pings,
/// and the watchdog drives overdue checks.
/// </summary>
public sealed class PushMonitorManager(
    HeartbeatWriter writer,
    NotificationDispatcher dispatcher,
    MonitorStateService state,
    ILogger<PushMonitorManager> log)
{
    /// <summary>URL path segment for the ping endpoint (shared by the endpoint route and the UI that shows the URL).</summary>
    public const string PingPathSegment = "ping";

    private readonly ConcurrentDictionary<int, Entry> _byId = new();
    private readonly ConcurrentDictionary<string, Entry> _byToken = new();

    /// <summary>Generate a fresh, unguessable ping token (128 bits, lower-hex).</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Add or refresh a push monitor from its current row. Idempotent; safe to call on every reload.</summary>
    public void Register(Monitor m)
    {
        Unregister(m.Id);

        var cfg = Deserialize(m.ConfigJson);
        if (string.IsNullOrEmpty(cfg.Token))
        {
            log.LogWarning("Push monitor {Id} '{Name}' has no token; it can never be pinged.", m.Id, m.Name);
            return;
        }

        var entry = new Entry
        {
            MonitorId = m.Id,
            Name = m.Name,
            Token = cfg.Token,
            Period = TimeSpan.FromSeconds(Math.Max(5, m.IntervalSeconds)),
            Grace = TimeSpan.FromSeconds(Math.Max(0, cfg.GraceSeconds)),
            // Seed from the last real ping so a restart doesn't lose the window; new monitors get a fresh grace.
            LastSeen = m.LastHeartbeatAt ?? DateTime.UtcNow,
            Machine = new MonitorStateMachine(retryCount: 0, upsideDown: false, resendEveryN: 0, initial: m.CurrentStatus),
        };
        _byId[m.Id] = entry;
        _byToken[cfg.Token] = entry;
    }

    public void Unregister(int monitorId)
    {
        if (_byId.TryRemove(monitorId, out var e))
            _byToken.TryRemove(e.Token, out _);
    }

    /// <summary>Record a received ping. Returns false if the token is unknown (so the endpoint can 404).</summary>
    public bool RecordPing(string token)
    {
        if (string.IsNullOrEmpty(token) || !_byToken.TryGetValue(token, out var e))
            return false;

        lock (e.Sync)
        {
            var now = DateTime.UtcNow;
            e.LastSeen = now;
            Emit(e, e.Machine.Evaluate(CheckStatus.Up), now, "Ping received");
        }
        return true;
    }

    /// <summary>Mark any push monitor whose next ping is overdue as down. Called on the watchdog tick.</summary>
    public void CheckOverdue()
    {
        var now = DateTime.UtcNow;
        foreach (var e in _byId.Values)
        {
            lock (e.Sync)
            {
                if (e.Machine.ConfirmedStatus == MonitorStatus.Down) continue; // already down; wait for a ping to recover
                var window = e.Period + e.Grace;
                if (now <= e.LastSeen + window) continue;
                Emit(e, e.Machine.Evaluate(CheckStatus.Down, hard: true), now,
                    $"No ping received within {window.TotalSeconds:0}s");
            }
        }
    }

    // Mirrors MonitorRunner.Process: persist the beat, update live state, and fire a notification on a transition.
    private void Emit(Entry e, StateDecision d, DateTime now, string message)
    {
        writer.Enqueue(new CheckOutcome(
            e.MonitorId, now, d.HeartbeatStatus, null, null, message,
            d.Important, d.Attempt, null, d.EventAction, d.PreviousConfirmed, d.NewConfirmed));

        state.ApplyResult(e.MonitorId, d.HeartbeatStatus, now, null, message, null);

        if (d.Notify != NotifyKind.None)
            dispatcher.Enqueue(new NotificationEvent(
                e.MonitorId, e.Name, d.NewConfirmed, d.PreviousConfirmed, now, message, null, d.Notify));

        if (d.EventAction != EventAction.None)
            log.LogInformation("Push monitor {Id} '{Name}': {From} -> {To} ({Message})",
                e.MonitorId, e.Name, d.PreviousConfirmed, d.NewConfirmed, message);
    }

    private static PushMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<PushMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }

    private sealed class Entry
    {
        public required int MonitorId { get; init; }
        public required string Name { get; init; }
        public required string Token { get; init; }
        public required TimeSpan Period { get; init; }
        public required TimeSpan Grace { get; init; }
        public required MonitorStateMachine Machine { get; init; }
        public DateTime LastSeen { get; set; }
        public object Sync { get; } = new();
    }
}

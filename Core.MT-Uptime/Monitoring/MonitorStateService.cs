using System.Collections.Concurrent;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Singleton cache of every monitor's latest status (plus a short recent-status buffer for the
/// dashboard sparkline), with a <see cref="Changed"/> event the UI subscribes to for live updates.
/// Also the read-through source for the UI so it renders instantly without hitting the database.
/// </summary>
public sealed class MonitorStateService
{
    private const int RecentMax = 40;
    private readonly ConcurrentDictionary<int, MonitorLiveState> _states = new();
    private readonly ConcurrentDictionary<int, List<MonitorStatus>> _recent = new();

    /// <summary>Raised with the monitor id whenever its live state changes, is seeded, or is removed.</summary>
    public event Action<int>? Changed;

    public IReadOnlyList<MonitorLiveState> Snapshot()
        => _states.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public MonitorLiveState? Get(int monitorId)
        => _states.TryGetValue(monitorId, out var s) ? s : null;

    /// <summary>Seed or refresh a monitor's metadata + last-known status from its database row.</summary>
    public void Seed(Monitor m)
    {
        _states[m.Id] = new MonitorLiveState
        {
            MonitorId = m.Id,
            Name = m.Name,
            Type = m.Type,
            Enabled = m.Enabled,
            Status = m.CurrentStatus,
            LastCheckAt = m.LastHeartbeatAt,
            LastResponseMs = m.LastResponseTimeMs,
            CertExpiresAt = m.CertExpiresAt,
            Recent = RecentSnapshot(m.Id),
        };
        Changed?.Invoke(m.Id);
    }

    /// <summary>Update the live status after a check completes.</summary>
    public void ApplyResult(int monitorId, MonitorStatus status, DateTime at, double? responseMs, string? message, DateTime? certExpiresAt)
    {
        var buffer = _recent.GetOrAdd(monitorId, static _ => new List<MonitorStatus>());
        IReadOnlyList<MonitorStatus> recent;
        lock (buffer)
        {
            buffer.Add(status);
            if (buffer.Count > RecentMax) buffer.RemoveRange(0, buffer.Count - RecentMax);
            recent = buffer.ToArray();
        }

        _states.AddOrUpdate(monitorId,
            _ => new MonitorLiveState
            {
                MonitorId = monitorId,
                Name = $"#{monitorId}",
                Status = status,
                LastCheckAt = at,
                LastResponseMs = responseMs,
                Message = message,
                CertExpiresAt = certExpiresAt,
                Recent = recent,
            },
            (_, prev) => prev with
            {
                Status = status,
                LastCheckAt = at,
                LastResponseMs = responseMs,
                Message = message,
                CertExpiresAt = certExpiresAt ?? prev.CertExpiresAt,
                Recent = recent,
            });
        Changed?.Invoke(monitorId);
    }

    public void Remove(int monitorId)
    {
        _states.TryRemove(monitorId, out _);
        _recent.TryRemove(monitorId, out _);
        Changed?.Invoke(monitorId);
    }

    private IReadOnlyList<MonitorStatus> RecentSnapshot(int monitorId)
    {
        if (_recent.TryGetValue(monitorId, out var buffer))
            lock (buffer) { return buffer.ToArray(); }
        return [];
    }
}

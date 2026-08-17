namespace MT.Uptime.Core.Monitoring.Configs;

/// <summary>
/// Settings for a push / heartbeat ("dead-man's switch") monitor, serialized into <c>Monitor.ConfigJson</c>.
/// The monitored job calls <c>/ping/{Token}</c> on a schedule; the monitor goes down when a ping is overdue
/// (see <see cref="MonitorStateMachine"/> and the push watchdog). The expected period is the monitor's
/// <c>IntervalSeconds</c>; <see cref="GraceSeconds"/> is the slack added on top before an alert fires.
/// </summary>
public sealed class PushMonitorConfig
{
    /// <summary>Unguessable URL token that identifies this monitor's ping endpoint. Generated once, then stable.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Extra slack (seconds) beyond the expected period before a missing ping is treated as down.</summary>
    public int GraceSeconds { get; set; } = 30;
}

namespace MT.Uptime.Core.Domain;

/// <summary>The kind of check a monitor performs. Stored as an int in the database.</summary>
public enum MonitorType
{
    Http = 0,
    Tcp = 1,
    Dns = 2,
    MySql = 3,
    Postgres = 4,
    Tls = 5,
    /// <summary>Passive "dead-man's switch": the target pings us on a schedule; we alert if a ping is overdue.</summary>
    Push = 6,
}

/// <summary>The raw outcome of a single probe (a checker never reports "pending").</summary>
public enum CheckStatus
{
    Down = 0,
    Up = 1,
}

/// <summary>
/// The persisted status of a monitor or heartbeat, including the retry-in-progress state
/// used by the up/down state machine before an outage is confirmed.
/// </summary>
public enum MonitorStatus
{
    Down = 0,
    Up = 1,
    Pending = 2,

    /// <summary>
    /// Responding, but slower than the monitor's configured threshold. Sits between Up and Down: the
    /// target is reachable and answering correctly, so this still counts as available for uptime %,
    /// but it is surfaced (and alerted on, once sustained) as a performance problem.
    /// </summary>
    Degraded = 3,
}

/// <summary>Delivery mechanism for a notification channel. Only Email is implemented in the MVP.</summary>
public enum NotificationChannelType
{
    Email = 0,
    Slack = 1,
    Webhook = 2,
    Telegram = 3,
    Discord = 4,
    Teams = 5,
    Ntfy = 6,
    Gotify = 7,
    PagerDuty = 8,
}

/// <summary>How a <see cref="MaintenanceWindow"/> repeats. Stored as an int.</summary>
public enum MaintenanceRecurrence
{
    /// <summary>A single period between two absolute instants.</summary>
    Once = 0,

    /// <summary>Opens on the days named by the day mask, at a local time of day. All seven days = daily.</summary>
    Weekly = 1,
}

/// <summary>Bucket size for an aggregated <see cref="StatRollup"/> row. Stored as an int.</summary>
public enum RollupPeriod
{
    Hourly = 0,
    Daily = 1,
}

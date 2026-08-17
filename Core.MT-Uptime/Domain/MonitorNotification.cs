namespace MT.Uptime.Core.Domain;

/// <summary>Join entity: which notification channels fire for which monitor.</summary>
public class MonitorNotification
{
    public int MonitorId { get; set; }
    public Monitor? Monitor { get; set; }

    public int NotificationChannelId { get; set; }
    public NotificationChannel? NotificationChannel { get; set; }
}

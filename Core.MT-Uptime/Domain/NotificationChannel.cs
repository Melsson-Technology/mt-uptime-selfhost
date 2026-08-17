namespace MT.Uptime.Core.Domain;

/// <summary>
/// A configured place to send alerts. The MVP ships Email (SendGrid); the type + JSON config
/// design lets Slack/webhook/Telegram slot in later without schema changes.
/// </summary>
public class NotificationChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType Type { get; set; }

    /// <summary>Channel configuration serialized as JSON. Secret fields are encrypted at rest.</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    /// <summary>When true, monitors with no explicit channel selection use this one.</summary>
    public bool IsDefault { get; set; }

    public ICollection<MonitorNotification> Monitors { get; set; } = new List<MonitorNotification>();
}

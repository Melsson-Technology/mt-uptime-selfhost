namespace MT.Uptime.Core.Notifications;

/// <summary>A pluggable alert-delivery mechanism (email, Slack, webhook, Telegram).</summary>
public interface INotificationChannel
{
    NotificationChannelType Type { get; }

    /// <summary>Delivers an alert for the event using the given channel configuration JSON. Returns success.</summary>
    Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct);
}

using System.Net.Http.Json;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>Posts an alert to a Slack incoming webhook.</summary>
public sealed class SlackNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Slack;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var url = Reveal(TryDeserialize<SlackChannelConfig>(configJson)?.WebhookUrl);
        if (string.IsNullOrWhiteSpace(url)) return false;

        var (tag, _) = NotificationRenderer.Describe(evt.Kind);
        var emoji = NotificationRenderer.SeverityOf(evt.Kind) switch
        {
            AlertSeverity.Good => ":large_green_circle:",
            AlertSeverity.Bad => ":red_circle:",
            // Amber, not red: the monitor is still answering. Without this it fell through to the
            // information icon and a real degradation read as an FYI.
            AlertSeverity.Warning => ":large_orange_circle:",
            _ => ":information_source:",
        };
        var text = $"{emoji} *{tag}: {evt.MonitorName}*\n{NotificationRenderer.PlainText(evt)}";

        var resp = await Http.PostAsJsonAsync(url, new { text }, ct);
        return resp.IsSuccessStatusCode;
    }
}

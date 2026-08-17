using System.Net.Http.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Posts an alert to a Discord incoming webhook as an embed.
/// <para>
/// An embed rather than plain <c>content</c> because Discord renders a coloured bar down its left edge,
/// which is the fastest way to tell "recovered" from "down" in a busy channel — the same job Slack's
/// emoji does.
/// </para>
/// </summary>
public sealed class DiscordNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Discord;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var url = Reveal(TryDeserialize<DiscordChannelConfig>(configJson)?.WebhookUrl);
        if (string.IsNullOrWhiteSpace(url)) return false;

        var (tag, _) = NotificationRenderer.Describe(evt.Kind);
        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"{tag}: {evt.MonitorName}",
                    description = NotificationRenderer.PlainText(evt),
                    color = ColourOf(NotificationRenderer.SeverityOf(evt.Kind)),
                },
            },
        };

        var resp = await Http.PostAsJsonAsync(url, payload, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Embed colour as the 24-bit integer Discord expects, not a CSS string.</summary>
    internal static int ColourOf(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Good => 0x2ECC71,      // green
        AlertSeverity.Bad => 0xE74C3C,       // red
        AlertSeverity.Warning => 0xE67E22,   // amber
        _ => 0x95A5A6,                       // grey
    };
}

using System.Net.Http.Json;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>Sends an alert via the Telegram Bot API (sendMessage).</summary>
public sealed class TelegramNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Telegram;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var cfg = TryDeserialize<TelegramChannelConfig>(configJson);
        var token = Reveal(cfg?.BotToken);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(cfg?.ChatId)) return false;

        var text = $"{NotificationRenderer.Subject(evt)}\n{NotificationRenderer.PlainText(evt)}";
        var resp = await Http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new { chat_id = cfg.ChatId, text }, ct);
        return resp.IsSuccessStatusCode;
    }
}

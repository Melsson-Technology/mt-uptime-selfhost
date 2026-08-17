using System.Text.Json;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Settings;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Email via the SendGrid Web API, awaited properly throughout. Config JSON is <see cref="EmailSettings"/>.
/// </summary>
public sealed class SendGridNotificationChannel(ILogger<SendGridNotificationChannel> log) : INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        EmailSettings? cfg;
        try { cfg = JsonSerializer.Deserialize<EmailSettings>(configJson); }
        catch { cfg = null; }

        if (cfg is null || !cfg.IsConfigured)
        {
            log.LogWarning("Email channel skipped: not fully configured.");
            return false;
        }

        var client = new SendGridClient(cfg.ApiKey);
        var from = new EmailAddress(cfg.FromEmail, string.IsNullOrWhiteSpace(cfg.FromName) ? "MT-Uptime" : cfg.FromName);
        var to = new EmailAddress(cfg.ToEmail);
        var msg = MailHelper.CreateSingleEmail(from, to,
            NotificationRenderer.Subject(evt), NotificationRenderer.PlainText(evt), NotificationRenderer.Html(evt));

        var resp = await client.SendEmailAsync(msg, ct);
        var ok = (int)resp.StatusCode is >= 200 and < 300;
        if (!ok)
        {
            var body = await resp.Body.ReadAsStringAsync(ct);
            log.LogError("SendGrid send failed ({Status}): {Body}", resp.StatusCode, body);
        }
        return ok;
    }
}

using System.Diagnostics;
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
            NotificationRenderer.Subject(evt), NotificationRenderer.PlainText(evt, NotificationRenderer.VerbosityFor(Type)), NotificationRenderer.Html(evt));

        var sw = Stopwatch.StartNew();
        var resp = await client.SendEmailAsync(msg, ct);
        sw.Stop();

        var ok = (int)resp.StatusCode is >= 200 and < 300;
        if (!ok)
        {
            var body = await resp.Body.ReadAsStringAsync(ct);
            log.LogError("SendGrid send failed ({Status}): {Body}", resp.StatusCode, body);
        }
        else
        {
            // Success logs, and it has to, because silence here is indistinguishable from "no alert was
            // ever attempted". Until 2026-09-11 this branch wrote nothing, so after an incident the box
            // could not answer "did the alert email go out?" — the first question anyone asks. Slack
            // leaves a trace only because its HttpClient is wired to RedactingHttpClientLogger;
            // SendGridClient builds its own client internally and never reaches that logger.
            //
            // "Accepted", not "sent" or "delivered": SendGrid answers 202 when it has queued the
            // message. What happens after that is between SendGrid and the recipient's mail server, and
            // a line claiming delivery would be the false comfort this whole feature exists to remove.
            //
            // The recipient is deliberately NOT logged. On the hosted runtime this same code runs once
            // per tenant and ToEmail is a paying customer's address, while this journal is shared with
            // six other services. The monitor name is enough to tie the line to its alert.
            log.LogInformation("Alert email for '{Monitor}' accepted by SendGrid ({Status}) in {Elapsed}ms",
                evt.MonitorName, (int)resp.StatusCode, sw.ElapsedMilliseconds);
        }
        return ok;
    }
}

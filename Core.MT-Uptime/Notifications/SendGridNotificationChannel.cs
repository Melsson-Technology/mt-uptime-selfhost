using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Settings;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Email via the SendGrid Web API, awaited properly throughout. Config JSON is <see cref="EmailSettings"/>.
/// <para>
/// The HTTP client is injected rather than left to the SDK to build. Two things follow from that, and
/// both were problems until 2026-09-11: the request now passes through
/// <see cref="RedactingHttpClientLogger"/> like every other channel, so a Slack delivery and an email
/// delivery leave the same kind of trace instead of one leaving none; and the send can be exercised
/// without a network call, which is why the success path has tests at all.
/// </para>
/// </summary>
public sealed class SendGridNotificationChannel(
    IHttpClientFactory httpFactory,
    ILogger<SendGridNotificationChannel> log) : INotificationChannel
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

        // A client per send, from the factory. It shares the pooled handler so this is cheap, and
        // nothing the SDK sets on the instance outlives the call. The named client is the notification
        // one, so this request is logged exactly like Slack's — host, status and timing, and never
        // headers. That last part is the bit worth knowing: the SendGrid credential travels in
        // Authorization, not in the URL, so the logger's path redaction is not what keeps it out of the
        // journal — the fact that it logs no headers at all is.
        //
        // Replacing the SDK's own client costs no retry behaviour. SendGrid's ReliabilitySettings
        // default MaximumNumberOfRetries to 0 — its own documentation says "no retries, you must
        // explicitly enable" — so nothing was retrying before this. If retries are ever wanted they
        // belong on the named client, where they are visible to anyone reading the registration.
        var http = httpFactory.CreateClient(WebhookChannelBase.HttpClientName);
        var client = new SendGridClient(http, cfg.ApiKey);

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
            // Kept even though the transport is now logged generically, because that line cannot name
            // the alert: "POST to api.sendgrid.com returned 202" does not say WHICH monitor's alert went
            // out, and that is the question asked after an incident. The two lines are complementary —
            // one proves the call happened, this one says what it was for.
            //
            // "Accepted", not "sent" or "delivered": SendGrid answers 202 once it has queued the
            // message. What follows is between SendGrid and the recipient's mail server, and a line
            // claiming delivery would be the false comfort this whole feature exists to remove.
            //
            // The recipient is deliberately NOT logged. On the hosted runtime this code runs once per
            // tenant and ToEmail is a paying customer's address, while this journal is shared with six
            // other services. The monitor name ties the line to its alert without naming anyone.
            log.LogInformation("Alert email for '{Monitor}' accepted by SendGrid ({Status}) in {Elapsed}ms",
                evt.MonitorName, (int)resp.StatusCode, sw.ElapsedMilliseconds);
        }
        return ok;
    }
}

using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Settings;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Sends a one-off transactional email to a named recipient, reusing the SendGrid credentials configured
/// for alerts. Distinct from <see cref="SendGridNotificationChannel"/>, which renders monitor alerts to
/// the fixed alert recipient — this exists for mail that is neither an alert nor addressed to that
/// recipient, currently password resets.
/// </summary>
public interface IEmailSender
{
    /// <summary>True when the configured credentials are complete enough to send at all.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>Sends the message. Returns false (and logs) rather than throwing on a delivery failure.</summary>
    Task<bool> SendAsync(string toEmail, string subject, string plainText, string html, CancellationToken ct = default);
}

/// <inheritdoc cref="IEmailSender"/>
public sealed class EmailSender(ISettingsService settings, ILogger<EmailSender> log) : IEmailSender
{
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var cfg = await settings.GetEmailAsync(ct);
        // Note: deliberately not EmailSettings.IsConfigured, which also demands a ToEmail. That field is
        // the *alert* recipient and is irrelevant here — we supply our own address.
        return !string.IsNullOrWhiteSpace(cfg.ApiKey) && !string.IsNullOrWhiteSpace(cfg.FromEmail);
    }

    public async Task<bool> SendAsync(
        string toEmail, string subject, string plainText, string html, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            log.LogWarning("Email send skipped: no recipient.");
            return false;
        }

        var cfg = await settings.GetEmailAsync(ct);
        if (string.IsNullOrWhiteSpace(cfg.ApiKey) || string.IsNullOrWhiteSpace(cfg.FromEmail))
        {
            log.LogWarning("Email send skipped: SendGrid is not configured (needs an API key and sender).");
            return false;
        }

        try
        {
            var client = new SendGridClient(cfg.ApiKey);
            var from = new EmailAddress(cfg.FromEmail, string.IsNullOrWhiteSpace(cfg.FromName) ? "MT-Uptime" : cfg.FromName);
            var msg = MailHelper.CreateSingleEmail(from, new EmailAddress(toEmail), subject, plainText, html);

            var resp = await client.SendEmailAsync(msg, ct);
            var ok = (int)resp.StatusCode is >= 200 and < 300;
            if (!ok)
            {
                var body = await resp.Body.ReadAsStringAsync(ct);
                log.LogError("SendGrid send failed ({Status}): {Body}", resp.StatusCode, body);
            }
            return ok;
        }
        catch (Exception ex)
        {
            // Never let a mail failure surface to the caller: the password-reset endpoint must answer
            // identically whether or not delivery worked, or it becomes an account-enumeration oracle.
            log.LogError(ex, "Email send to {Recipient} threw.", toEmail);
            return false;
        }
    }
}

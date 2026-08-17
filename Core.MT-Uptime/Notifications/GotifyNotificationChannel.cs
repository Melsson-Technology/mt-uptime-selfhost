using System.Net.Http.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Posts an alert to a self-hosted Gotify server.
/// <para>
/// The token goes in the <c>X-Gotify-Key</c> header rather than the <c>?token=</c> query parameter that
/// Gotify's own examples use. Both work, but a query string ends up in access logs and proxy logs, and
/// this one is a credential — the same reason webhook URLs are kept out of our own logs.
/// </para>
/// </summary>
public sealed class GotifyNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Gotify;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var cfg = TryDeserialize<GotifyChannelConfig>(configJson);
        var token = Reveal(cfg?.AppToken);
        if (string.IsNullOrWhiteSpace(cfg?.ServerUrl) || string.IsNullOrWhiteSpace(token)) return false;

        if (!Uri.TryCreate($"{cfg.ServerUrl.TrimEnd('/')}/message", UriKind.Absolute, out var uri))
            return false;

        var (tag, _) = NotificationRenderer.Describe(evt.Kind);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                title = $"{tag}: {evt.MonitorName}",
                message = NotificationRenderer.PlainText(evt),
                priority = PriorityOf(NotificationRenderer.SeverityOf(evt.Kind)),
            }),
        };
        req.Headers.TryAddWithoutValidation("X-Gotify-Key", token);

        var resp = await Http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Gotify priority. 8 and above is what its Android client treats as an urgent notification.</summary>
    internal static int PriorityOf(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Bad => 8,
        AlertSeverity.Warning => 5,
        AlertSeverity.Good => 3,
        _ => 3,
    };
}

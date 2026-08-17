using System.Net.Http.Headers;
using System.Text;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Publishes an alert to an ntfy topic.
/// <para>
/// ntfy takes the message as the raw request body and everything else as headers, which is why this is
/// the one channel here that does not post JSON. Priority matters more than it looks: ntfy's Android
/// client only bypasses Do Not Disturb at priority 5, so a Down alert at the default priority is an
/// alert your phone will sit on until morning.
/// </para>
/// </summary>
public sealed class NtfyNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Ntfy;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var cfg = TryDeserialize<NtfyChannelConfig>(configJson);
        var url = Reveal(cfg?.TopicUrl);
        if (string.IsNullOrWhiteSpace(url)) return false;

        var (tag, _) = NotificationRenderer.Describe(evt.Kind);
        var severity = NotificationRenderer.SeverityOf(evt.Kind);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(NotificationRenderer.PlainText(evt), Encoding.UTF8, "text/plain"),
        };

        // Header values must be Latin-1-safe: ntfy expects RFC 2047 encoding for anything else, and a
        // monitor named with non-ASCII characters would otherwise throw on the way out.
        req.Headers.TryAddWithoutValidation("Title", Ascii($"{tag}: {evt.MonitorName}"));
        req.Headers.TryAddWithoutValidation("Priority", PriorityOf(severity).ToString());
        req.Headers.TryAddWithoutValidation("Tags", TagOf(severity));

        if (Reveal(cfg?.AccessToken) is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await Http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>ntfy priority 1–5. 5 is the only one that breaks through Do Not Disturb.</summary>
    internal static int PriorityOf(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Bad => 5,        // urgent — wake someone up
        AlertSeverity.Warning => 4,    // high
        AlertSeverity.Good => 3,       // default: recovery is good news, not an emergency
        _ => 3,
    };

    /// <summary>An ntfy tag that resolves to an emoji in the client.</summary>
    internal static string TagOf(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Good => "white_check_mark",
        AlertSeverity.Bad => "rotating_light",
        AlertSeverity.Warning => "warning",
        _ => "information_source",
    };

    /// <summary>Strips characters that cannot travel in a header value, rather than failing the send.</summary>
    private static string Ascii(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(c <= 0xFF ? c : '?');
        return sb.ToString();
    }
}

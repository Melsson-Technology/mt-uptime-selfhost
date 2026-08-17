using System.Net.Http.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Posts an alert to a Microsoft Teams webhook as an Adaptive Card.
/// <para>
/// <b>Adaptive Card, not the older MessageCard.</b> Most "Teams webhook" examples still show
/// <c>@type: MessageCard</c>, which is the Office 365 connector format — the thing Microsoft has been
/// retiring. Its replacement is a Power Automate "Workflows" webhook, which expects the
/// <c>type: message</c> + <c>attachments</c> envelope below. Writing MessageCard today means shipping
/// against the deprecated path and breaking for anyone who has already migrated.
/// </para>
/// </summary>
public sealed class TeamsNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Teams;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var url = Reveal(TryDeserialize<TeamsChannelConfig>(configJson)?.WebhookUrl);
        if (string.IsNullOrWhiteSpace(url)) return false;

        var (tag, _) = NotificationRenderer.Describe(evt.Kind);
        var payload = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        // No "$schema": it is optional for rendering, and a C# anonymous type cannot
                        // produce a property name starting with '$' — emitting plain "schema" would be
                        // a subtly wrong key rather than an omitted optional one.
                        version = "1.4",
                        body = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = $"{tag}: {evt.MonitorName}",
                                weight = "Bolder",
                                size = "Medium",
                                wrap = true,
                                color = ColourOf(NotificationRenderer.SeverityOf(evt.Kind)),
                            },
                            new
                            {
                                type = "TextBlock",
                                text = NotificationRenderer.PlainText(evt),
                                wrap = true,
                            },
                        },
                    },
                },
            },
        };

        var resp = await Http.PostAsJsonAsync(url, payload, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Adaptive Cards take named colours from a fixed set, not hex.</summary>
    internal static string ColourOf(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Good => "Good",
        AlertSeverity.Bad => "Attention",
        AlertSeverity.Warning => "Warning",
        _ => "Default",
    };
}

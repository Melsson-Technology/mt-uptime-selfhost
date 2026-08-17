namespace MT.Uptime.Core.Notifications;

// Type-specific notification-channel settings, serialized into NotificationChannel.ConfigJson.
// Secret fields are stored encrypted (Data Protection) and decrypted by the channel at send time.

public sealed class SlackChannelConfig
{
    /// <summary>Slack incoming-webhook URL (encrypted at rest).</summary>
    public string? WebhookUrl { get; set; }
}

public sealed class WebhookChannelConfig
{
    /// <summary>Target URL that receives a JSON POST for each alert (encrypted at rest).</summary>
    public string? Url { get; set; }
}

public sealed class TelegramChannelConfig
{
    /// <summary>Bot token (encrypted at rest).</summary>
    public string? BotToken { get; set; }
    public string? ChatId { get; set; }
}

public sealed class DiscordChannelConfig
{
    /// <summary>Discord incoming-webhook URL (encrypted at rest — the URL is the credential).</summary>
    public string? WebhookUrl { get; set; }
}

public sealed class TeamsChannelConfig
{
    /// <summary>Microsoft Teams webhook URL (encrypted at rest — the URL is the credential).</summary>
    public string? WebhookUrl { get; set; }
}

public sealed class NtfyChannelConfig
{
    /// <summary>
    /// Full topic URL, e.g. <c>https://ntfy.sh/my-alerts</c>. Encrypted at rest because on a public
    /// ntfy server the topic name <em>is</em> the access control — anyone who knows it can publish to
    /// you and subscribe to your alerts.
    /// </summary>
    public string? TopicUrl { get; set; }

    /// <summary>Optional bearer token, for a self-hosted ntfy with auth enabled (encrypted at rest).</summary>
    public string? AccessToken { get; set; }
}

public sealed class GotifyChannelConfig
{
    /// <summary>Base URL of the Gotify server, e.g. <c>https://gotify.example.com</c>. Not a secret.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Application token (encrypted at rest).</summary>
    public string? AppToken { get; set; }
}

public sealed class PagerDutyChannelConfig
{
    /// <summary>Events API v2 integration/routing key (encrypted at rest).</summary>
    public string? RoutingKey { get; set; }
}

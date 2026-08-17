using System.Text.Json;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>Shared plumbing for HTTP-based notification channels: a pooled client, secret decryption, and config parsing.</summary>
public abstract class WebhookChannelBase(IHttpClientFactory httpFactory, ISecretProtector protector)
{
    public const string HttpClientName = "notify";

    protected HttpClient Http => httpFactory.CreateClient(HttpClientName);

    protected string? Reveal(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        try { return protector.Unprotect(cipher); }
        catch { return null; }
    }

    protected static T? TryDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }
}

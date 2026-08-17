using System.Net;
using System.Text.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Tests;

/// <summary>
/// The Slack payload is the only place a <see cref="NotifyKind"/> is translated into something a human
/// reads at a glance, and the mapping is a switch with a fallback — so adding a kind and forgetting the
/// switch is silent. Degraded shipped exactly that way and posted the information icon, which reads as
/// an FYI rather than an alert. These tests pin every kind to its colour.
/// </summary>
public class SlackPayloadTests
{
    [Theory]
    [InlineData(NotifyKind.Up, ":large_green_circle:")]
    [InlineData(NotifyKind.Down, ":red_circle:")]
    [InlineData(NotifyKind.ResendDown, ":red_circle:")]
    [InlineData(NotifyKind.Degraded, ":large_orange_circle:")]
    public async Task Each_kind_posts_its_own_icon(NotifyKind kind, string expected)
    {
        var (channel, handler) = NewChannel();

        var sent = await channel.SendAsync(Event(kind), Config, CancellationToken.None);

        Assert.True(sent);
        Assert.StartsWith(expected, TextOf(handler.LastBody!));
    }

    [Fact]
    public async Task Degraded_is_not_the_generic_information_icon()
    {
        // The regression itself, stated as its own assertion: falling through the switch is the failure
        // mode, and it is invisible in a green/red-only test.
        var (channel, handler) = NewChannel();

        await channel.SendAsync(Event(NotifyKind.Degraded), Config, CancellationToken.None);

        Assert.DoesNotContain(":information_source:", TextOf(handler.LastBody!));
    }

    [Fact]
    public async Task The_message_names_the_monitor_and_says_what_happened()
    {
        // An icon alone is not an alert. Whoever is on call needs the name and the transition in the
        // first line, because that is all Slack shows in a notification preview.
        var (channel, handler) = NewChannel();

        await channel.SendAsync(Event(NotifyKind.Degraded), Config, CancellationToken.None);

        var text = TextOf(handler.LastBody!);
        Assert.Contains("SLOW", text);
        Assert.Contains("api.example.com", text);
        Assert.Contains("2,400 ms", text);
    }

    [Fact]
    public async Task A_channel_with_no_webhook_configured_reports_failure_without_calling_out()
    {
        var (channel, handler) = NewChannel();

        var sent = await channel.SendAsync(Event(NotifyKind.Down), """{"WebhookUrl":""}""", CancellationToken.None);

        Assert.False(sent);
        Assert.Null(handler.LastBody);
    }

    // --- helpers ---------------------------------------------------------------------------------

    private const string Config = """{"WebhookUrl":"https://hooks.slack.com/services/T0/B0/tok"}""";

    private static NotificationEvent Event(NotifyKind kind) => new(
        MonitorId: 1,
        MonitorName: "api.example.com",
        NewStatus: MonitorStatus.Degraded,
        OldStatus: MonitorStatus.Up,
        At: new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc),
        Message: null,
        ResponseTimeMs: 2400,
        Kind: kind);

    private static string TextOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;

    private static (SlackNotificationChannel, CapturingHandler) NewChannel()
    {
        var handler = new CapturingHandler();
        return (new SlackNotificationChannel(new StubHttpClientFactory(handler), new PassthroughProtector()), handler);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // The channel decrypts the stored webhook before use; these tests are about the payload, not the
    // Data Protection round trip, so the config above holds the URL in the clear.
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }
}

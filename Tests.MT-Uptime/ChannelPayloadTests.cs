using System.Net;
using System.Text.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Tests;

/// <summary>
/// Payloads for the channels added alongside Slack. Each service has its own vocabulary for "how bad is
/// this" — an embed colour, an Adaptive Card colour name, a priority number, a PagerDuty severity — and
/// each mapping is a switch with a fallback arm, which is the shape that let Degraded ship as Slack's
/// information icon. These pin them.
/// </summary>
public class ChannelPayloadTests
{
    // --- The mapping every channel now shares -----------------------------------------------------

    [Theory]
    [InlineData(NotifyKind.Up, AlertSeverity.Good)]
    [InlineData(NotifyKind.Down, AlertSeverity.Bad)]
    [InlineData(NotifyKind.ResendDown, AlertSeverity.Bad)]
    [InlineData(NotifyKind.Degraded, AlertSeverity.Warning)]
    public void Each_kind_maps_to_its_severity(NotifyKind kind, AlertSeverity expected)
        => Assert.Equal(expected, NotificationRenderer.SeverityOf(kind));

    [Fact]
    public void No_alerting_kind_falls_through_to_Info()
    {
        // The guard that makes the shared mapping worth having: add a NotifyKind, forget this switch,
        // and every channel at once starts calling a real alert "information". Asserting per-kind above
        // cannot catch it, because a new kind has no InlineData row until someone writes one.
        foreach (var kind in Enum.GetValues<NotifyKind>())
        {
            if (kind == NotifyKind.None) continue;   // None is never dispatched
            Assert.NotEqual(AlertSeverity.Info, NotificationRenderer.SeverityOf(kind));
        }
    }

    // --- Discord ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(NotifyKind.Up, 0x2ECC71)]
    [InlineData(NotifyKind.Down, 0xE74C3C)]
    [InlineData(NotifyKind.Degraded, 0xE67E22)]
    public async Task Discord_posts_an_embed_coloured_by_severity(NotifyKind kind, int expected)
    {
        var (channel, handler) = Discord();

        var sent = await channel.SendAsync(Event(kind), """{"WebhookUrl":"https://discord.com/api/webhooks/1/x"}""", CancellationToken.None);

        Assert.True(sent);
        var embed = Root(handler).GetProperty("embeds")[0];
        Assert.Equal(expected, embed.GetProperty("color").GetInt32());
        Assert.Contains("api.example.com", embed.GetProperty("title").GetString());
    }

    // --- Teams ------------------------------------------------------------------------------------

    [Fact]
    public async Task Teams_posts_an_adaptive_card_not_the_retired_message_card()
    {
        // The Office 365 connector format (@type: MessageCard) is what most examples still show, and it
        // is the path Microsoft is retiring. Shipping it would break on any workspace already migrated.
        var (channel, handler) = Teams();

        await channel.SendAsync(Event(NotifyKind.Down), """{"WebhookUrl":"https://example.logic.azure.com/x"}""", CancellationToken.None);

        var root = Root(handler);
        Assert.Equal("message", root.GetProperty("type").GetString());
        var content = root.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("AdaptiveCard", content.GetProperty("type").GetString());
        Assert.Equal("Attention", content.GetProperty("body")[0].GetProperty("color").GetString());

        var raw = handler.LastBody!;
        Assert.DoesNotContain("MessageCard", raw);
        // "schema" without the '$' would be a silently wrong key rather than an omitted optional one.
        Assert.DoesNotContain("\"schema\"", raw);
    }

    // --- ntfy -------------------------------------------------------------------------------------

    [Fact]
    public async Task Ntfy_sends_the_message_as_the_body_with_metadata_in_headers()
    {
        var (channel, handler) = Ntfy();

        var sent = await channel.SendAsync(Event(NotifyKind.Down), """{"TopicUrl":"https://ntfy.sh/alerts"}""", CancellationToken.None);

        Assert.True(sent);
        Assert.Contains("api.example.com", handler.LastBody);
        Assert.Equal("5", handler.HeaderValue("Priority"));            // urgent: the only priority that beats DND
        Assert.Equal("rotating_light", handler.HeaderValue("Tags"));
        Assert.Contains("DOWN", handler.HeaderValue("Title"));
        Assert.Null(handler.LastAuthorization);                        // no token configured, no header
    }

    [Fact]
    public async Task Ntfy_sends_a_bearer_token_when_one_is_configured()
    {
        var (channel, handler) = Ntfy();

        await channel.SendAsync(
            Event(NotifyKind.Down),
            """{"TopicUrl":"https://ntfy.example.com/alerts","AccessToken":"tk_secret"}""",
            CancellationToken.None);

        Assert.Equal("Bearer tk_secret", handler.LastAuthorization);
    }

    [Fact]
    public async Task Ntfy_survives_a_monitor_name_that_cannot_travel_in_a_header()
    {
        // Header values are Latin-1. A monitor named in, say, Japanese would otherwise throw on the way
        // out and take the alert with it — the notification matters more than the exact title.
        var (channel, handler) = Ntfy();
        var evt = Event(NotifyKind.Down) with { MonitorName = "監視対象" };

        var sent = await channel.SendAsync(evt, """{"TopicUrl":"https://ntfy.sh/alerts"}""", CancellationToken.None);

        Assert.True(sent);
        Assert.Contains("監視対象", handler.LastBody);   // the body is UTF-8, so the real name survives there
    }

    // --- Gotify -----------------------------------------------------------------------------------

    [Fact]
    public async Task Gotify_puts_the_token_in_a_header_not_the_query_string()
    {
        // A ?token= would be written to access logs and proxy logs, which is the same mistake the
        // redacting HTTP logger exists to prevent on our side.
        var (channel, handler) = Gotify();

        var sent = await channel.SendAsync(
            Event(NotifyKind.Down),
            """{"ServerUrl":"https://gotify.example.com/","AppToken":"AbC123"}""",
            CancellationToken.None);

        Assert.True(sent);
        Assert.Equal("AbC123", handler.HeaderValue("X-Gotify-Key"));
        Assert.Equal("https://gotify.example.com/message", handler.LastUrl);   // trailing slash collapsed
        Assert.DoesNotContain("token=", handler.LastUrl!);
        Assert.Equal(8, Root(handler).GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Gotify_without_a_server_url_reports_failure_without_calling_out()
    {
        var (channel, handler) = Gotify();

        var sent = await channel.SendAsync(Event(NotifyKind.Down), """{"AppToken":"AbC123"}""", CancellationToken.None);

        Assert.False(sent);
        Assert.Null(handler.LastBody);
    }

    // --- Webhook ----------------------------------------------------------------------------------

    [Fact]
    public async Task Webhook_carries_the_failing_checks_evidence()
    {
        var (channel, handler) = Webhook();

        await channel.SendAsync(Event(NotifyKind.Down) with { Diagnostics = Evidence() }, WebhookConfig, CancellationToken.None);

        var evidence = Root(handler).GetProperty("evidence");
        Assert.Equal(CfRay, evidence.GetProperty("headers").GetProperty("cf-ray").GetString());
        Assert.Equal("Web server is down", evidence.GetProperty("bodySnippet").GetString());
        Assert.Equal("1.1", evidence.GetProperty("httpVersion").GetString());
        var timings = evidence.GetProperty("timings");
        Assert.Equal(21400, timings.GetProperty("connectMs").GetDouble());
        Assert.Equal(21556, timings.GetProperty("totalMs").GetDouble());
        Assert.False(timings.GetProperty("connectionReused").GetBoolean());
    }

    [Fact]
    public async Task Webhook_evidence_is_null_when_the_check_collected_none()
    {
        // Present and null rather than absent, like incident and diagnostics: a consumer can tell "this
        // check gathered nothing" from "this build predates the field".
        var (channel, handler) = Webhook();

        await channel.SendAsync(Event(NotifyKind.Down), WebhookConfig, CancellationToken.None);

        Assert.Equal(JsonValueKind.Null, Root(handler).GetProperty("evidence").ValueKind);
    }

    // --- PagerDuty --------------------------------------------------------------------------------

    [Fact]
    public async Task PagerDuty_triggers_on_down_with_a_stable_dedup_key()
    {
        var (channel, handler) = PagerDuty();

        var sent = await channel.SendAsync(Event(NotifyKind.Down), PagerDutyConfig, CancellationToken.None);

        Assert.True(sent);
        var root = Root(handler);
        Assert.Equal("trigger", root.GetProperty("event_action").GetString());
        Assert.Equal("critical", root.GetProperty("payload").GetProperty("severity").GetString());
        Assert.Equal("mt-uptime-monitor-1", root.GetProperty("dedup_key").GetString());
    }

    [Fact]
    public async Task PagerDuty_resolves_on_recovery_rather_than_paging_again()
    {
        // The whole reason to integrate with PagerDuty rather than post a message: a recovery has to
        // close the incident and stop the escalation. A second trigger would page on-call about a
        // service that is already back, which is how an integration gets muted.
        var (channel, handler) = PagerDuty();

        await channel.SendAsync(Event(NotifyKind.Up), PagerDutyConfig, CancellationToken.None);

        var root = Root(handler);
        Assert.Equal("resolve", root.GetProperty("event_action").GetString());
        Assert.False(root.TryGetProperty("payload", out _));   // a resolve carries no payload
    }

    [Fact]
    public async Task PagerDuty_resolves_against_the_same_incident_it_opened()
    {
        // Trigger and resolve must agree on dedup_key, or the incident stays open forever. Anything
        // time-varying in that key — a timestamp, a random id — breaks this and nothing else would show it.
        var (down, downHandler) = PagerDuty();
        var (up, upHandler) = PagerDuty();

        await down.SendAsync(Event(NotifyKind.Down), PagerDutyConfig, CancellationToken.None);
        await up.SendAsync(Event(NotifyKind.Up), PagerDutyConfig, CancellationToken.None);

        Assert.Equal(
            Root(downHandler).GetProperty("dedup_key").GetString(),
            Root(upHandler).GetProperty("dedup_key").GetString());
    }

    [Fact]
    public async Task PagerDuty_dedup_keys_differ_between_monitors()
    {
        var (a, handlerA) = PagerDuty();
        var (b, handlerB) = PagerDuty();

        await a.SendAsync(Event(NotifyKind.Down), PagerDutyConfig, CancellationToken.None);
        await b.SendAsync(Event(NotifyKind.Down) with { MonitorId = 2 }, PagerDutyConfig, CancellationToken.None);

        Assert.NotEqual(
            Root(handlerA).GetProperty("dedup_key").GetString(),
            Root(handlerB).GetProperty("dedup_key").GetString());
    }

    [Fact]
    public async Task PagerDuty_trigger_carries_the_failing_checks_evidence()
    {
        var (channel, handler) = PagerDuty();

        await channel.SendAsync(Event(NotifyKind.Down) with { Diagnostics = Evidence() }, PagerDutyConfig, CancellationToken.None);

        var evidence = Root(handler).GetProperty("payload").GetProperty("custom_details").GetProperty("evidence");
        Assert.Equal(CfRay, evidence.GetProperty("response_headers").GetProperty("cf-ray").GetString());
        Assert.Equal("Web server is down", evidence.GetProperty("body_snippet").GetString());
        Assert.Equal(21556, evidence.GetProperty("timings").GetProperty("total_ms").GetDouble());
    }

    // --- Every channel handles a missing secret the same way --------------------------------------

    [Fact]
    public async Task A_channel_with_nothing_configured_reports_failure_without_calling_out()
    {
        // "{}" is what an unfinished channel row looks like. None of these may throw, and none may
        // reach the network — a half-configured channel must not become an exception in the dispatcher.
        var handler = new CapturingHandler();
        var factory = new StubHttpClientFactory(handler);
        var protector = new PassthroughProtector();

        INotificationChannel[] channels =
        [
            new DiscordNotificationChannel(factory, protector),
            new TeamsNotificationChannel(factory, protector),
            new NtfyNotificationChannel(factory, protector),
            new GotifyNotificationChannel(factory, protector),
            new PagerDutyNotificationChannel(factory, protector),
        ];

        foreach (var channel in channels)
        {
            Assert.False(await channel.SendAsync(Event(NotifyKind.Down), "{}", CancellationToken.None),
                $"{channel.Type} claimed success with no configuration");
            Assert.Null(handler.LastBody);
        }
    }

    // --- A channel declared Rich must actually carry the evidence ----------------------------------

    [Fact]
    public async Task Every_channel_declared_Rich_carries_the_evidence()
    {
        // VerbosityFor is a promise about the payload, not a label. Webhook and PagerDuty were declared
        // Rich while building their own JSON without the evidence, so the cf-ray Slack carried inline never
        // reached the two consumers able to use it structured, and nothing noticed. This pins the promise
        // to the bytes on the wire. Email is the one Rich type not here: it is a SendGrid message rather
        // than an HTTP channel, and AlertEmailTests covers what it renders.
        var handler = new CapturingHandler();
        var factory = new StubHttpClientFactory(handler);
        var protector = new PassthroughProtector();

        (INotificationChannel Channel, string Config)[] channels =
        [
            (new SlackNotificationChannel(factory, protector), """{"WebhookUrl":"https://hooks.slack.com/services/T0/B0/tok"}"""),
            (new DiscordNotificationChannel(factory, protector), """{"WebhookUrl":"https://discord.com/api/webhooks/1/x"}"""),
            (new TeamsNotificationChannel(factory, protector), """{"WebhookUrl":"https://example.logic.azure.com/x"}"""),
            (new WebhookNotificationChannel(factory, protector), WebhookConfig),
            (new PagerDutyNotificationChannel(factory, protector), PagerDutyConfig),
        ];

        // A new Rich type must be added above, not silently left out of the check.
        var rich = Enum.GetValues<NotificationChannelType>()
            .Where(t => t != NotificationChannelType.Email && NotificationRenderer.VerbosityFor(t) == AlertVerbosity.Rich)
            .Order();
        Assert.Equal(rich, channels.Select(c => c.Channel.Type).Order());

        foreach (var (channel, config) in channels)
        {
            Assert.True(await channel.SendAsync(Event(NotifyKind.Down) with { Diagnostics = Evidence() }, config, CancellationToken.None),
                $"{channel.Type} did not send");
            Assert.True(handler.LastBody!.Contains(CfRay, StringComparison.Ordinal), $"{channel.Type} dropped the cf-ray");
            Assert.True(handler.LastBody.Contains("Web server is down", StringComparison.Ordinal), $"{channel.Type} dropped the response body");
        }
    }

    // "Every channel type has an implementation" lives in WebhookLoggingTests, resolved from the real
    // container — a hand-written list here would pass while the registration was missing, which is the
    // failure that actually ships.

    // --- helpers ---------------------------------------------------------------------------------

    private const string PagerDutyConfig = """{"RoutingKey":"R0UT1NGK3Y"}""";
    private const string WebhookConfig = """{"Url":"https://hooks.example.com/mt-uptime"}""";
    private const string CfRay = "9c1a2b3c4d5e6f70-LHR";

    /// <summary>What a failing HTTP check behind Cloudflare keeps: the case the evidence exists for.</summary>
    private static CheckDiagnostics Evidence() => new()
    {
        Timings = new ProbeTimingBreakdown(12, 21400, 8, 21500, 21556),
        Headers = new Dictionary<string, string> { ["cf-ray"] = CfRay, ["server"] = "cloudflare" },
        BodySnippet = "Web server is down",
        HttpVersion = "1.1",
    };

    private static NotificationEvent Event(NotifyKind kind) => new(
        MonitorId: 1,
        MonitorName: "api.example.com",
        NewStatus: MonitorStatus.Down,
        OldStatus: MonitorStatus.Up,
        At: new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc),
        Message: "connection refused",
        ResponseTimeMs: 2400,
        Kind: kind);

    private static JsonElement Root(CapturingHandler handler) => JsonDocument.Parse(handler.LastBody!).RootElement;

    private static StubHttpClientFactory Factory() => new(new CapturingHandler());
    private static PassthroughProtector Protector() => new();

    private static (DiscordNotificationChannel, CapturingHandler) Discord()
    {
        var h = new CapturingHandler();
        return (new DiscordNotificationChannel(new StubHttpClientFactory(h), new PassthroughProtector()), h);
    }

    private static (TeamsNotificationChannel, CapturingHandler) Teams()
    {
        var h = new CapturingHandler();
        return (new TeamsNotificationChannel(new StubHttpClientFactory(h), new PassthroughProtector()), h);
    }

    private static (NtfyNotificationChannel, CapturingHandler) Ntfy()
    {
        var h = new CapturingHandler();
        return (new NtfyNotificationChannel(new StubHttpClientFactory(h), new PassthroughProtector()), h);
    }

    private static (GotifyNotificationChannel, CapturingHandler) Gotify()
    {
        var h = new CapturingHandler();
        return (new GotifyNotificationChannel(new StubHttpClientFactory(h), new PassthroughProtector()), h);
    }

    private static (WebhookNotificationChannel, CapturingHandler) Webhook()
    {
        var h = new CapturingHandler();
        return (new WebhookNotificationChannel(new StubHttpClientFactory(h), new PassthroughProtector()), h);
    }

    private static (PagerDutyNotificationChannel, CapturingHandler) PagerDuty()
    {
        var h = new CapturingHandler();
        return (new PagerDutyNotificationChannel(new StubHttpClientFactory(h), new PassthroughProtector()), h);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string? HeaderValue(string name) => _headers.TryGetValue(name, out var v) ? v : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            _headers.Clear();
            foreach (var h in request.Headers) _headers[h.Key] = string.Join(", ", h.Value);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>These tests are about the payload, not the Data Protection round trip.</summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }
}

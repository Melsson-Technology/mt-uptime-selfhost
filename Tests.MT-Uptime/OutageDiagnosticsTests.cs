using System.Net;
using System.Text.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Tests;

/// <summary>
/// Covers the evidence a failing HTTP probe now keeps, and what the alert does with it.
/// <para>
/// The case throughout is the real one this work came from: "On the Hook Fish &amp; Chips" reported
/// DOWN with a 21,556 ms response, <c>Unexpected status 521</c>, and nothing else. Everything needed to
/// name the cause was in the response and was being discarded.
/// </para>
/// </summary>
public class OutageDiagnosticsTests
{
    // --- Capture ------------------------------------------------------------------------------------

    /// <summary>
    /// Cloudflare's actual 521 shape: its own headers, and an error page whose useful content is one
    /// sentence wrapped in several kilobytes of markup.
    /// </summary>
    private const string CloudflareBody = """
        <!DOCTYPE html><html><head><title>onthehook.co.uk | 521: Web server is down</title>
        <style>body{font-family:sans-serif}</style></head><body>
        <h1>Error 521</h1><h2>Web server is down</h2>
        <p>The web server is not returning a connection. As a result, the web page is not displaying.</p>
        </body></html>
        """;

    [Fact]
    public async Task A_cloudflare_521_keeps_the_ray_id_the_server_header_and_the_error_text()
    {
        var r = await RunAsync(
            Cfg(),
            HttpStatusCode.TooManyRequests, // placeholder; overridden below
            status: 521,
            body: CloudflareBody,
            headers: new()
            {
                ["cf-ray"] = "9c1a4f2b8e7d0a13-LHR",
                ["server"] = "cloudflare",
                ["cf-cache-status"] = "DYNAMIC",
                // Must NOT be captured: not on the allowlist, and it is a credential.
                ["set-cookie"] = "session=super-secret-value; HttpOnly",
                ["x-internal-backend"] = "origin-pool-3.internal",
            });

        Assert.Equal(CheckStatus.Down, r.Status);
        Assert.Equal("521", r.StatusCode);

        var d = Assert.IsType<CheckDiagnostics>(r.Diagnostics);

        // The identifier Cloudflare support asks for — and the edge datacenter, LHR, comes free with it.
        Assert.Equal("9c1a4f2b8e7d0a13-LHR", d.Headers["cf-ray"]);
        Assert.Equal("cloudflare", d.Headers["server"]);

        // The allowlist is a disclosure boundary, not just a size limit.
        Assert.DoesNotContain("set-cookie", d.Headers.Keys);
        Assert.DoesNotContain("x-internal-backend", d.Headers.Keys);
        Assert.DoesNotContain("super-secret-value", JsonSerializer.Serialize(d));

        // The page's sentence survives; its markup does not.
        Assert.NotNull(d.BodySnippet);
        Assert.Contains("Web server is down", d.BodySnippet);
        Assert.Contains("not returning a connection", d.BodySnippet);
        Assert.DoesNotContain("<", d.BodySnippet);
        Assert.DoesNotContain("font-family", d.BodySnippet);
    }

    [Fact]
    public async Task A_healthy_check_gathers_nothing_and_never_reads_the_body()
    {
        // The check path is the hot path. A monitor that is fine must not pay for diagnostics, and the
        // body in particular must not be pulled over the wire for no reason.
        var content = new ThrowingContent();
        var r = await RunAsync(Cfg(), HttpStatusCode.OK, content: content);

        Assert.Equal(CheckStatus.Up, r.Status);
        Assert.Null(r.Diagnostics);
        Assert.False(content.WasRead, "a successful check must not read the response body");
    }

    [Fact]
    public async Task A_hostile_body_cannot_inflate_the_alert()
    {
        // CheckResult.MaxMessageLength exists because a target that can pad the outbound alert past a
        // channel's payload limit suppresses the notification about its own outage. The snippet is
        // bounded for the same reason.
        var r = await RunAsync(Cfg(), HttpStatusCode.ServiceUnavailable, body: new string('A', 512 * 1024));

        var d = Assert.IsType<CheckDiagnostics>(r.Diagnostics);
        Assert.NotNull(d.BodySnippet);
        Assert.True(
            d.BodySnippet!.Length <= CheckDiagnostics.MaxBodySnippet,
            $"snippet was {d.BodySnippet.Length} chars");
    }

    [Fact]
    public async Task A_redirect_that_moved_us_is_reported_and_one_that_did_not_is_not()
    {
        var landed = await RunAsync(Cfg(), HttpStatusCode.ServiceUnavailable, finalUrl: "http://monitor.test/login");
        Assert.Equal("http://monitor.test/login", Assert.IsType<CheckDiagnostics>(landed.Diagnostics).FinalUrl);

        // Echoing back the URL the operator just read in the same alert is noise.
        var stayed = await RunAsync(Cfg(), HttpStatusCode.ServiceUnavailable, finalUrl: "http://monitor.test");
        Assert.Null(Assert.IsType<CheckDiagnostics>(stayed.Diagnostics).FinalUrl);
    }

    // --- The timing breakdown -----------------------------------------------------------------------

    [Fact]
    public void A_pooled_connection_reports_reuse_rather_than_pretending_the_legs_took_no_time()
    {
        // Zeros here would read as "DNS was instant" when the truth is "there was no DNS lookup".
        var reused = new ProbeTimingBreakdown(null, null, null, 21500, 21556);
        Assert.True(reused.ConnectionReused);

        var fresh = new ProbeTimingBreakdown(12, 21400, 8, 21500, 21556);
        Assert.False(fresh.ConnectionReused);
    }

    [Fact]
    public void The_timing_legs_are_collected_from_the_handler_callbacks()
    {
        var timings = ProbeTimings.Begin();
        try
        {
            Assert.Same(timings, ProbeTimings.Current);

            timings.RecordDns(TimeSpan.FromMilliseconds(12));
            timings.RecordConnect(TimeSpan.FromMilliseconds(340));
            timings.RecordHandshakeComplete();

            var b = timings.ToBreakdown(totalMs: 500, ttfbMs: 480);
            Assert.Equal(12, b.DnsMs);
            Assert.Equal(340, b.ConnectMs);
            Assert.NotNull(b.TlsMs);          // measured as the gap after connect
            Assert.Equal(480, b.TimeToFirstByteMs);
            Assert.Equal(500, b.TotalMs);
            Assert.False(b.ConnectionReused);
        }
        finally
        {
            ProbeTimings.End();
        }

        // Cleared, so a later probe on a recycled thread cannot write into the finished one.
        Assert.Null(ProbeTimings.Current);
    }

    // --- Persistence --------------------------------------------------------------------------------

    [Fact]
    public void Diagnostics_round_trip_through_the_heartbeat_column_and_tolerate_junk()
    {
        var original = new CheckDiagnostics
        {
            Timings = new ProbeTimingBreakdown(12, 21400, 8, 21500, 21556),
            Headers = new Dictionary<string, string> { ["cf-ray"] = "9c1a-LHR" },
            BodySnippet = "Web server is down",
            HttpVersion = "1.1",
        };

        var restored = CheckDiagnostics.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal("9c1a-LHR", restored!.Headers["cf-ray"]);
        Assert.Equal(21400, restored.Timings!.ConnectMs);
        Assert.Equal("Web server is down", restored.BodySnippet);

        // Nothing to say means no column write at all, so healthy beats stay NULL.
        Assert.Null(new CheckDiagnostics().ToJson());

        // A row written by another build must never break the page reading it.
        Assert.Null(CheckDiagnostics.FromJson("{ not json"));
        Assert.Null(CheckDiagnostics.FromJson(null));
        Assert.Null(CheckDiagnostics.FromJson(""));
    }

    // --- Rendering ----------------------------------------------------------------------------------

    /// <summary>
    /// The whole point, assembled: the alert that started this, rendered with everything now captured.
    /// </summary>
    [Fact]
    public void The_on_the_hook_alert_now_names_the_cause()
    {
        var text = NotificationRenderer.PlainText(OnTheHook(), AlertVerbosity.Rich);

        // What it always said.
        Assert.Contains("On the Hook Fish & Chips is DOWN.", text);
        Assert.Contains("Detail: Unexpected status 521", text);

        // What it can now say. This is the line that changes what happens next.
        Assert.Contains("the origin refused the connection", text);

        // Where the 21,556 ms actually went — the TCP connect, not DNS and not the server thinking.
        Assert.Contains("Where the time went:", text);
        Assert.Contains("DNS 12 ms", text);
        Assert.Contains("connect 21,400 ms", text);

        // The reference Cloudflare support can act on, and the proof an intermediary answered.
        Assert.Contains("cf-ray: 9c1a4f2b8e7d0a13-LHR", text);
        Assert.Contains("server: cloudflare", text);

        // The origin's own words.
        Assert.Contains("Response body: Error 521 Web server is down", text);

        // And the baseline it broke from, no longer racing the heartbeat writer.
        Assert.Contains("Last good response: 200, 46 ms", text);
    }

    [Fact]
    public void A_push_channel_gets_the_diagnosis_and_a_link_rather_than_the_whole_block()
    {
        var evt = OnTheHook();

        var terse = NotificationRenderer.PlainText(evt, AlertVerbosity.Terse);
        var rich = NotificationRenderer.PlainText(evt, AlertVerbosity.Rich);

        // The one-line diagnosis is never dropped — it is the reason the alert is worth reading.
        Assert.Contains("the origin refused the connection", terse);

        // But the block that would blow an ntfy or Telegram payload limit is.
        Assert.DoesNotContain("Where the time went", terse);
        Assert.DoesNotContain("cf-ray", terse);
        Assert.DoesNotContain("Response body:", terse);

        Assert.Contains("Full diagnostics: https://uptime.example.com/monitors/7", terse);
        Assert.True(terse.Length < rich.Length);
    }

    [Fact]
    public void With_no_public_origin_configured_a_terse_alert_carries_no_link_rather_than_a_broken_one()
    {
        var evt = OnTheHook() with { DetailsUrl = null };
        Assert.DoesNotContain("Full diagnostics", NotificationRenderer.PlainText(evt, AlertVerbosity.Terse));
    }

    [Fact]
    public void Every_channel_type_declares_a_verbosity()
    {
        // The sibling of "no dispatched kind falls through to Info". VerbosityFor has no default arm, so
        // this fails to compile rather than fails at runtime if a channel is added — but a channel added
        // with a careless copy-paste would still show up here as the wrong answer for a push target.
        foreach (var type in Enum.GetValues<NotificationChannelType>())
        {
            var verbosity = NotificationRenderer.VerbosityFor(type);
            Assert.True(Enum.IsDefined(verbosity), $"{type} maps to an undefined verbosity");
        }

        // The three that are read on a lock screen and enforce payload limits.
        Assert.Equal(AlertVerbosity.Terse, NotificationRenderer.VerbosityFor(NotificationChannelType.Ntfy));
        Assert.Equal(AlertVerbosity.Terse, NotificationRenderer.VerbosityFor(NotificationChannelType.Telegram));
        Assert.Equal(AlertVerbosity.Terse, NotificationRenderer.VerbosityFor(NotificationChannelType.Gotify));

        Assert.Equal(AlertVerbosity.Rich, NotificationRenderer.VerbosityFor(NotificationChannelType.Email));
        Assert.Equal(AlertVerbosity.Rich, NotificationRenderer.VerbosityFor(NotificationChannelType.Slack));
    }

    [Fact]
    public void The_html_body_escapes_the_evidence_it_quotes()
    {
        // The body snippet is target-controlled text going into an HTML email.
        var evt = OnTheHook() with
        {
            Diagnostics = new CheckDiagnostics { BodySnippet = "<script>alert('xss')</script>" },
        };

        var html = NotificationRenderer.Html(evt);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static NotificationEvent OnTheHook() =>
        new(7, "On the Hook Fish & Chips", MonitorStatus.Down, MonitorStatus.Up,
            new DateTime(2026, 9, 10, 0, 25, 42, DateTimeKind.Utc),
            "Unexpected status 521", 21556, NotifyKind.Down)
        {
            StatusCode = "521",
            DetailsUrl = "https://uptime.example.com/monitors/7",
            Enrichment = new AlertEnrichment(
                "104.20.38.178", "200", 46,
                new DateTime(2026, 9, 10, 0, 24, 42, DateTimeKind.Utc),
                [45, 75, 54, 64, 46],
                null,
                new DateTime(2026, 9, 5, 21, 25, 42, DateTimeKind.Utc)),
            Diagnostics = new CheckDiagnostics
            {
                Timings = new ProbeTimingBreakdown(12, 21400, null, 21550, 21556),
                Headers = new Dictionary<string, string>
                {
                    ["cf-ray"] = "9c1a4f2b8e7d0a13-LHR",
                    ["server"] = "cloudflare",
                },
                BodySnippet = "Error 521 Web server is down The web server is not returning a connection.",
                HttpVersion = "1.1",
            },
        };

    private static HttpMonitorConfig Cfg(string accepted = "200-299")
        => new() { Url = "http://monitor.test", AcceptedStatusCodes = accepted };

    private static async Task<CheckResult> RunAsync(
        HttpMonitorConfig cfg,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        int? status = null,
        string body = "",
        Dictionary<string, string>? headers = null,
        string? finalUrl = null,
        HttpContent? content = null)
    {
        var handler = new HeaderStubHandler(status ?? (int)statusCode, body, headers, finalUrl, content);
        var checker = new HttpChecker(new StubFactory(handler), new Passthrough());
        var ctx = new MonitorContext(1, "test", MonitorType.Http, TimeSpan.FromSeconds(5), JsonSerializer.Serialize(cfg));
        return await checker.CheckAsync(ctx, CancellationToken.None);
    }

    /// <summary>A canned response that can carry arbitrary headers and a chosen final URL.</summary>
    private sealed class HeaderStubHandler(
        int status, string body, Dictionary<string, string>? headers, string? finalUrl, HttpContent? content)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = content ?? new StringContent(body),
                // What the handler reports as the request it ultimately performed — this is how a
                // redirect-following client tells the caller where it ended up.
                RequestMessage = finalUrl is null
                    ? request
                    : new HttpRequestMessage(request.Method, finalUrl),
            };

            foreach (var (name, value) in headers ?? [])
                resp.Headers.TryAddWithoutValidation(name, value);

            return Task.FromResult(resp);
        }
    }

    /// <summary>Content that records whether anyone read it, to prove the happy path does not.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Passthrough : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }
}

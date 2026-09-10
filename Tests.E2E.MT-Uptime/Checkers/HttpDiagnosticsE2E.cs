using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Checkers;

/// <summary>
/// The evidence a failing HTTP check keeps — response headers, a body snippet, the final URL, and the
/// per-leg timing breakdown — against real servers rather than a stub.
/// <para>
/// <b>Why this file exists.</b> The battery ran green on 2026-09-10 for the release that added this
/// feature and proved nothing whatsoever about it: no test named it, and the checker tier did not
/// mention it once. The capture had to be verified by hand, by pointing monitors at broken targets and
/// reading <c>Heartbeats.Diagnostics</c> back out of SQLite. That is not a thing to repeat per release.
/// </para>
/// <para>
/// <b>And a stub cannot replace it.</b> Two defects in this feature were invisible to the hermetic
/// suite and both needed a real socket. A real <c>Server: nginx/1.24.0 (Ubuntu)</c> was being mangled
/// to <c>nginx/1.24.0, (Ubuntu)</c>, because .NET parses that header as a product list and every
/// stubbed test used the single token <c>cloudflare</c>. And a failed DNS lookup reported itself as
/// <i>"connection reused"</i>, because the timing legs are written from inside <c>ConnectCallback</c>
/// and a lookup that throws leaves it the same way a pooled connection never enters it — every leg
/// null. Neither is reachable without a server that behaves like a server.
/// </para>
/// </summary>
public class HttpDiagnosticsE2E : IClassFixture<CheckerHost>
{
    private readonly IMonitorChecker _http;

    public HttpDiagnosticsE2E(CheckerHost host) => _http = host.For(MonitorType.Http);

    private Task<CheckResult> ProbeAsync(string url, Action<HttpMonitorConfig>? configure = null)
    {
        var cfg = new HttpMonitorConfig { Url = url };
        configure?.Invoke(cfg);
        return Probe.RunAsync(_http, Probe.Context(MonitorType.Http, cfg));
    }

    private static string Url(string path) => Targets.HttpBaseUrl + path;

    // ── the happy path pays nothing ────────────────────────────────────────────────────────────

    [E2EFact]
    public async Task A_healthy_check_gathers_no_diagnostics_at_all()
    {
        // The central performance claim of the feature, and the one a stub cannot make credible: this
        // is a real server sending a real body, and the checker must not read or keep any of it.
        var result = await ProbeAsync(Url("/ok"));

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Null(result.Diagnostics);
    }

    // ── a status the server chose ──────────────────────────────────────────────────────────────

    [E2EFact]
    public async Task A_failing_status_keeps_the_server_header_the_body_and_the_timings()
    {
        // 521 is the shape that motivated the feature: Cloudflare's, not a standard code, meaning the
        // edge is fine and the origin refused. The fixture emits it verbatim.
        var result = await ProbeAsync(Url("/status/521"));

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("521", result.StatusCode);

        var d = Assert.IsType<CheckDiagnostics>(result.Diagnostics);

        // Behind nginx, so `server` is present and is exactly the header nginx sent.
        Assert.True(d.Headers.ContainsKey("server"), "a real nginx sends Server; it must be captured");
        var server = d.Headers["server"];
        Assert.False(string.IsNullOrWhiteSpace(server));

        // The regression that shipped: .NET parses Server as a product list, so a value like
        // "nginx/1.24.0 (Ubuntu)" came back as two elements and was rejoined with a comma the origin
        // never sent. Any real Server header carrying a version and a platform shows it.
        Assert.DoesNotContain(", (", server);

        Assert.NotNull(d.BodySnippet);
        Assert.NotNull(d.Timings);
        Assert.True(d.Timings!.TotalMs > 0, "a completed request took some measurable time");

        // It has to survive the round trip through the heartbeat column, which is where the incident
        // page reads it from later.
        var json = d.ToJson();
        Assert.NotNull(json);
        Assert.NotNull(CheckDiagnostics.FromJson(json));
    }

    [E2EFact]
    public async Task A_broken_target_is_described_from_what_it_actually_said()
    {
        // Broken by the battery's own helper rather than by asking for an error page, so the fixture is
        // failing the way `break http` makes real services fail for every other tier.
        using var _ = TargetControl.Break(Target.Http);

        var result = await ProbeAsync(Url("/toggle"));

        Assert.Equal(CheckStatus.Down, result.Status);

        var d = Assert.IsType<CheckDiagnostics>(result.Diagnostics);
        Assert.NotNull(d.BodySnippet);
        Assert.NotNull(d.Timings);
    }

    // ── where the time went, and where it stopped ──────────────────────────────────────────────

    [E2EFact]
    public async Task A_name_that_does_not_resolve_records_a_dns_leg_and_is_not_called_a_reused_connection()
    {
        // The defect this test exists for. ConnectionReused is inferred from every leg being null, and
        // a DNS failure used to leave them all null — so the alert said the network setup path was
        // ruled out on the one probe where DNS *was* the fault. The manifest's NXDOMAIN name is served
        // by the box's own authoritative zone, so this is a real negative answer, not a typo.
        var result = await ProbeAsync($"http://{Targets.DnsNxdomainName}/");

        Assert.Equal(CheckStatus.Down, result.Status);

        var d = Assert.IsType<CheckDiagnostics>(result.Diagnostics);
        var t = Assert.IsType<ProbeTimingBreakdown>(d.Timings);

        Assert.NotNull(t.DnsMs);
        Assert.Null(t.ConnectMs);
        Assert.False(t.ConnectionReused, "resolution was attempted and failed — nothing was reused");
    }

    [E2EFact]
    public async Task A_refused_port_records_how_long_the_connect_leg_took()
    {
        // "Refused after twenty seconds in the TCP connect" and "failed instantly at DNS" arrive as the
        // same message, and separating them is the whole reason the breakdown exists. A failed connect
        // used to record no leg at all, so it could not.
        var result = await ProbeAsync($"http://{Targets.Host}:{Targets.TcpRefusedPort}/ok");

        Assert.Equal(CheckStatus.Down, result.Status);

        var d = Assert.IsType<CheckDiagnostics>(result.Diagnostics);
        var t = Assert.IsType<ProbeTimingBreakdown>(d.Timings);

        Assert.NotNull(t.DnsMs);
        Assert.NotNull(t.ConnectMs);
        Assert.Null(t.TlsMs);
        Assert.False(t.ConnectionReused);
    }

    [E2EFact]
    public async Task An_expired_certificate_stops_after_connect_and_names_the_certificate()
    {
        // The commonest way a monitored endpoint actually breaks. Two things are asserted together
        // because they were two separate defects: the legs must localise the failure to the handshake,
        // and the message must carry the reason rather than an instruction to look elsewhere.
        //
        // `localhost`, not Targets.Host, and for the same reason HttpCheckerE2E uses it for every HTTPS
        // case: the battery's certificates are issued for that name. Against the IP the handshake would
        // fail on a NAME MISMATCH instead, which also says "certificate" and would let this pass while
        // testing something else entirely.
        var result = await ProbeAsync($"https://localhost:{Targets.HttpsExpiredPort}/ok");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains("certificate", result.Message!, StringComparison.OrdinalIgnoreCase);

        var d = Assert.IsType<CheckDiagnostics>(result.Diagnostics);
        var t = Assert.IsType<ProbeTimingBreakdown>(d.Timings);

        // Got as far as connecting and no further — TLS never completed, so there is no handshake to
        // time. That absence is the diagnosis.
        Assert.NotNull(t.ConnectMs);
        Assert.Null(t.TlsMs);
        Assert.False(t.ConnectionReused);
    }

    // ── the disclosure boundary, against a server that really sets the headers ─────────────────

    [E2EFact]
    public async Task Only_allowlisted_headers_are_captured_from_a_real_response()
    {
        var result = await ProbeAsync(Url("/status/503"));

        var d = Assert.IsType<CheckDiagnostics>(result.Diagnostics);

        // Whatever the real server sent, nothing outside the allowlist may be kept. `date` and
        // `content-type` are on every one of this fixture's responses and are not on the list.
        foreach (var name in d.Headers.Keys)
            Assert.Contains(name, CheckDiagnostics.InterestingHeaders, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("date", d.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("content-type", d.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task A_redirect_that_moved_the_request_records_where_it_ended_up()
    {
        // The fixture's /redirect goes to /ok, which is a 200 — so to see a FinalUrl on a *failing*
        // check the accepted range has to exclude it. That is the real shape of the invisible outage
        // this field exists for: a monitor followed a redirect somewhere unintended and called it fine.
        var result = await ProbeAsync(Url("/redirect"), c => c.AcceptedStatusCodes = "500-599");

        Assert.Equal(CheckStatus.Down, result.Status);

        var d = Assert.IsType<CheckDiagnostics>(result.Diagnostics);
        Assert.NotNull(d.FinalUrl);
        Assert.EndsWith("/ok", d.FinalUrl!, StringComparison.Ordinal);
    }
}

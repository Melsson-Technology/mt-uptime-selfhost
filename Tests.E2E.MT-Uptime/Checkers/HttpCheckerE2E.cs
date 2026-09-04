using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Security;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Checkers;

/// <summary>
/// <see cref="HttpChecker"/> against a real HTTP server.
/// <para>
/// The hermetic suite covers this checker better than the other five — but through a stubbed
/// <c>HttpMessageHandler</c>, which means it tests the checker's logic and never the four pooled
/// clients <c>AddMonitoringEngine</c> registers. Those clients are where <c>FollowRedirects</c> and
/// <c>IgnoreTlsErrors</c> actually live: they are properties of a handler, not of a request, so the
/// checker cannot express them itself and a stub cannot observe them. Everything in this file that
/// touches a redirect or a certificate is testing a registration a unit test structurally cannot
/// reach.
/// </para>
/// </summary>
public class HttpCheckerE2E : IClassFixture<CheckerHost>
{
    private readonly IMonitorChecker _http;
    private readonly ISecretProtector _protector;

    public HttpCheckerE2E(CheckerHost host)
    {
        _http = host.For(MonitorType.Http);
        _protector = host.Protector;
    }

    private static string Base => Targets.HttpBaseUrl;

    private Task<CheckResult> ProbeAsync(HttpMonitorConfig cfg, TimeSpan? cancelAfter = null) =>
        Probe.RunAsync(_http, Probe.Context(MonitorType.Http, cfg), cancelAfter);

    private Task<CheckResult> GetAsync(string path, Action<HttpMonitorConfig>? configure = null, TimeSpan? cancelAfter = null)
    {
        var cfg = new HttpMonitorConfig { Url = path.StartsWith("http", StringComparison.Ordinal) ? path : Base + path };
        configure?.Invoke(cfg);
        return ProbeAsync(cfg, cancelAfter);
    }

    // ── status codes ───────────────────────────────────────────────────────────────────────────

    [E2EFact]
    public async Task A_healthy_endpoint_is_Up_with_its_status_code()
    {
        var result = await GetAsync("/ok");

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal("200", result.StatusCode);
        Assert.Null(result.Message);
        Assert.NotNull(result.ResponseTimeMs);
    }

    [E2EFact]
    public async Task An_unaccepted_status_is_a_HARD_Down()
    {
        // The only hard Down any checker produces, and the reason it exists: the server answered. It
        // made a definitive statement about its own health, and waiting out three more retries to hear
        // it repeat itself delays every HTTP alert for no information.
        //
        // This is the single most consequential branch in the checker — it decides how fast the
        // commonest kind of outage is reported — and until this file it was only ever exercised
        // against a stub.
        var result = await GetAsync("/status/500");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.True(result.Hard, "a status the server actually sent must confirm Down immediately");
        Assert.Equal("Unexpected status 500", result.Message);
        Assert.Equal("500", result.StatusCode);
    }

    [E2ETheory]
    [InlineData("500", "500")]
    [InlineData("500", "200-599")]
    [InlineData("404", "200-299,404")]
    [InlineData("301", "301")]
    public async Task An_accepted_status_is_Up_however_odd_it_looks(string status, string accepted)
    {
        // AcceptedStatusCodes is what makes a monitor for an endpoint that legitimately answers 404,
        // or an API gateway that answers 401 to an unauthenticated probe. Ranges and single values mix.
        var result = await GetAsync($"/status/{status}", c => c.AcceptedStatusCodes = accepted);

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal(status, result.StatusCode);
    }

    [E2EFact]
    public async Task A_status_outside_a_narrowed_range_is_Down()
    {
        // The negative control for the theory above: the same route, the same checker, an acceptance
        // list that excludes it. Without this, every row above would pass on a checker that accepted
        // everything.
        var result = await GetAsync("/status/204", c => c.AcceptedStatusCodes = "200");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.True(result.Hard);
        Assert.Equal("Unexpected status 204", result.Message);
    }

    // ── keyword ────────────────────────────────────────────────────────────────────────────────

    [E2EFact]
    public async Task A_present_keyword_is_Up()
    {
        var result = await GetAsync("/ok", c => c.Keyword = Targets.Keyword);

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_missing_keyword_is_a_SOFT_Down()
    {
        // Soft, unlike a bad status. The server answered 200, so the disagreement is about content —
        // a page mid-deploy serving a placeholder, say — and that is the sort of thing a retry
        // legitimately resolves.
        var result = await GetAsync("/ok", c => c.Keyword = "definitely-not-in-this-body");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.Equal("Keyword \"definitely-not-in-this-body\" not found", result.Message);
        Assert.Equal("200", result.StatusCode);
    }

    [E2EFact]
    public async Task Keyword_matching_ignores_case()
    {
        var result = await GetAsync("/ok", c => c.Keyword = Targets.Keyword.ToLowerInvariant());

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task An_inverted_keyword_fails_when_the_word_is_present()
    {
        // The "our error page must not be showing" monitor. Inverted means the check fails when the
        // word IS there.
        var result = await GetAsync("/ok", c =>
        {
            c.Keyword = Targets.Keyword;
            c.KeywordInverted = true;
        });

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.Contains("present", result.Message, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task An_inverted_keyword_passes_when_the_word_is_absent()
    {
        var result = await GetAsync("/ok", c =>
        {
            c.Keyword = "definitely-not-in-this-body";
            c.KeywordInverted = true;
        });

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task The_status_check_happens_before_the_keyword_check()
    {
        // Ordering matters for the alert an operator receives. A server returning 500 with an error
        // page that happens to lack the keyword should report the status — the actionable fact — not
        // the keyword. It also decides hardness: status-first means this confirms Down immediately.
        var result = await GetAsync("/status/500", c => c.Keyword = "definitely-not-in-this-body");

        Assert.Equal("Unexpected status 500", result.Message);
        Assert.True(result.Hard);
    }

    // ── redirects: the four pooled clients ─────────────────────────────────────────────────────

    [E2EFact]
    public async Task A_redirect_is_followed_by_default()
    {
        // FollowRedirects defaults true, and the answer comes back as 200 from /ok rather than 302.
        var result = await GetAsync("/redirect");

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal("200", result.StatusCode);
    }

    [E2EFact]
    public async Task A_redirect_is_a_hard_Down_when_following_is_off()
    {
        // The monitor that catches a site quietly 302-ing to a maintenance page or a login form. With
        // following off the 302 is the answer, and 302 is outside 200-299.
        var result = await GetAsync("/redirect", c => c.FollowRedirects = false);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.True(result.Hard);
        Assert.Equal("302", result.StatusCode);
    }

    [E2EFact]
    public async Task A_redirect_can_be_accepted_explicitly()
    {
        var result = await GetAsync("/redirect", c =>
        {
            c.FollowRedirects = false;
            c.AcceptedStatusCodes = "302";
        });

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task An_endless_redirect_loop_gives_up_and_reports_the_last_3xx()
    {
        // CORRECTED PREDICTION. The plan expected a soft Down carrying a "too many redirects" message
        // from an exception. SocketsHttpHandler does not throw when it exhausts MaxAutomaticRedirections
        // — it returns the final 3xx response — so this comes back through the ordinary
        // unaccepted-status path: a HARD Down naming the code.
        //
        // Worth knowing because the two differ in when the alert fires, not just in wording.
        var result = await GetAsync("/redirect-loop");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.True(result.Hard);
        Assert.Equal("Unexpected status 302", result.Message);
    }

    // ── TLS: the other axis of those four clients ──────────────────────────────────────────────

    [E2EFact]
    public async Task A_trusted_certificate_is_Up_over_HTTPS()
    {
        // Depends on install-targets.sh having put our CA in the system store; without that this is
        // the same as the untrusted case below.
        var result = await GetAsync($"https://localhost:{Targets.HttpsValidPort}/ok");

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal("200", result.StatusCode);
    }

    [E2ETheory]
    [InlineData("HTTPS_UNTRUSTED_PORT")]
    [InlineData("HTTPS_EXPIRED_PORT")]
    public async Task A_certificate_the_box_rejects_is_a_soft_Down(string portKey)
    {
        // Soft: a handshake failure is a transport problem, not the server's verdict on itself.
        var result = await GetAsync($"https://localhost:{Targets.Int(portKey)}/ok");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);

        // A PRODUCT FINDING, now FIXED — and this assertion is the record of both halves.
        //
        // It used to read `DoesNotContain("certificate")`, pinning the defect: CheckResult.Down kept
        // only ex.Message, and for a rejected server certificate that is "The SSL connection could not
        // be established, see inner exception." The words "certificate", "expired" and "chain" were
        // all in the inner AuthenticationException, which was discarded — on the commonest HTTPS
        // monitor failure there is.
        //
        // ProbeFailure.Describe now walks the inner chain, so the reason survives into the alert. The
        // outer sentence is still there, because it is true and an operator scanning for "SSL" should
        // find it; what follows it is the part that says what to do.
        Assert.Contains("SSL connection could not be established", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("certificate", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [E2ETheory]
    [InlineData("HTTPS_UNTRUSTED_PORT")]
    [InlineData("HTTPS_EXPIRED_PORT")]
    public async Task IgnoreTlsErrors_makes_a_rejected_certificate_Up(string portKey)
    {
        var result = await GetAsync($"https://localhost:{Targets.Int(portKey)}/ok", c => c.IgnoreTlsErrors = true);

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal("200", result.StatusCode);
    }

    [E2EFact]
    public async Task IgnoreTlsErrors_does_not_quietly_turn_redirect_following_back_on()
    {
        // A REGRESSION TEST FOR A REAL BUG the product already fixed, and the one case in this file
        // that a stubbed handler could never have caught.
        //
        // ClientNameFor once tested IgnoreTlsErrors first and stopped there, so ticking "ignore TLS
        // certificate errors" silently restored redirect-following. A monitor whose operator had
        // explicitly UNTICKED "follow redirects" — to catch exactly a 302 to a login page — would
        // follow it, report Up, and keep the outage invisible.
        //
        // The two toggles are independent in the editor, so they must be independent here, and that is
        // only observable through the pooled clients the container registers.
        var result = await GetAsync($"https://localhost:{Targets.HttpsUntrustedPort}/redirect", c =>
        {
            c.IgnoreTlsErrors = true;
            c.FollowRedirects = false;
        });

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("302", result.StatusCode);
    }

    // ── authentication ─────────────────────────────────────────────────────────────────────────

    [E2EFact]
    public async Task Basic_auth_with_the_right_credentials_is_Up()
    {
        var result = await GetAsync("/basic", c =>
        {
            c.AuthMode = HttpAuthMode.Basic;
            c.AuthUsername = Targets.BasicUser;
            c.AuthSecret = _protector.Protect(Targets.BasicPassword);
        });

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task Basic_auth_with_a_wrong_password_is_a_hard_Down()
    {
        var result = await GetAsync("/basic", c =>
        {
            c.AuthMode = HttpAuthMode.Basic;
            c.AuthUsername = Targets.BasicUser;
            c.AuthSecret = _protector.Protect("wrong");
        });

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.True(result.Hard);
        Assert.Equal("401", result.StatusCode);
    }

    [E2EFact]
    public async Task No_auth_against_a_protected_endpoint_is_a_hard_Down()
    {
        var result = await GetAsync("/basic");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("401", result.StatusCode);
    }

    [E2EFact]
    public async Task Bearer_auth_with_the_right_token_is_Up()
    {
        var result = await GetAsync("/bearer", c =>
        {
            c.AuthMode = HttpAuthMode.Bearer;
            c.AuthSecret = _protector.Protect(Targets.BearerToken);
        });

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task Bearer_auth_with_a_wrong_token_is_a_hard_Down()
    {
        var result = await GetAsync("/bearer", c =>
        {
            c.AuthMode = HttpAuthMode.Bearer;
            c.AuthSecret = _protector.Protect("not-the-token");
        });

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("401", result.StatusCode);
    }

    // ── what actually goes on the wire ─────────────────────────────────────────────────────────
    //
    // /echo reflects the request back as JSON, and every assertion below is made through the checker's
    // OWN keyword search of that reply. Nothing here rebuilds the request by hand.
    //
    // That constraint is the whole point. A test that sent its own HttpRequestMessage to /echo and
    // compared the two would be asserting that the test's reimplementation of BuildRequest matches the
    // test's reimplementation of BuildRequest — the same mistake a stubbed handler makes, dressed up
    // as an integration test. Going through Keyword means the bytes being searched are the bytes the
    // product put on the wire, and the only code involved is the product's.
    //
    // The cost is that a failure reports "Keyword not found" rather than a diff. Assert.Equal would
    // read better on a bad day and would be testing nothing on a good one.

    [E2EFact]
    public async Task The_keyword_assertion_can_actually_fail()
    {
        // The negative control for every test in this section. If /echo answered 200 with an empty
        // body, or the keyword search were broken, all of them would pass while proving nothing —
        // so this one asserts a string that must NOT be in the echo of a plain GET.
        var absent = await EchoAsync("\"x-api-key\"");
        var present = await EchoAsync("\"method\"");

        Assert.Equal(CheckStatus.Down, absent.Status);
        Assert.Equal(CheckStatus.Up, present.Status);
    }

    [E2EFact]
    public async Task A_request_body_reaches_the_server()
    {
        var result = await EchoAsync("mt-uptime-e2e-body-marker", c =>
        {
            c.Method = "POST";
            c.Body = """{"probe":"mt-uptime-e2e-body-marker"}""";
        });

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task The_method_reaches_the_server()
    {
        var result = await EchoAsync("\"method\": \"PUT\"", c => c.Method = "PUT");

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task The_content_type_accompanies_the_body()
    {
        var result = await EchoAsync("\"content-type\": \"text/csv", c =>
        {
            c.Method = "POST";
            c.Body = "a,b,c";
            c.ContentType = "text/csv";
        });

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task Custom_headers_reach_the_server()
    {
        var result = await EchoAsync("\"x-api-key\": \"e2e-secret-value\"",
            c => c.Headers = _protector.Protect("X-API-Key: e2e-secret-value"));

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task The_default_User_Agent_identifies_the_product()
    {
        // A request with no User-Agent looks like an anonymous scraper and gets 403 from a lot of
        // WAFs, so the checker always sends one.
        var result = await EchoAsync($"\"user-agent\": \"{HttpChecker.UserAgent}\"");

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_per_monitor_User_Agent_replaces_the_default()
    {
        // The escape hatch for a target whose WAF allowlists one specific UA — an endpoint that is
        // otherwise unmonitorable. It has to REPLACE rather than append, which is why the second
        // assertion matters as much as the first: two User-Agent values would fail the allowlist just
        // as surely as the wrong one.
        var replaced = await EchoAsync("\"user-agent\": \"e2e-custom-agent/9.9\"",
            c => c.UserAgent = "e2e-custom-agent/9.9");
        var defaultGone = await EchoAsync(HttpChecker.UserAgent,
            c => c.UserAgent = "e2e-custom-agent/9.9");

        Assert.Equal(CheckStatus.Up, replaced.Status);
        Assert.Equal(CheckStatus.Down, defaultGone.Status);
    }

    [E2EFact]
    public async Task A_custom_Authorization_header_beats_the_AuthMode()
    {
        // ApplyHeader removes before adding, specifically so a custom line wins rather than producing
        // two Authorization values. Without that, a target seeing both would reject the request and
        // the escape hatch would be useless for the case it exists for — a signing scheme the product
        // does not model.
        void Both(HttpMonitorConfig c)
        {
            c.AuthMode = HttpAuthMode.Bearer;
            c.AuthSecret = _protector.Protect("token-from-authmode");
            c.Headers = _protector.Protect("Authorization: Custom overriding-value");
        }

        var custom = await EchoAsync("\"authorization\": \"Custom overriding-value\"", Both);
        var bearerGone = await EchoAsync("token-from-authmode", Both);

        Assert.Equal(CheckStatus.Up, custom.Status);
        Assert.Equal(CheckStatus.Down, bearerGone.Status);
    }

    [E2EFact]
    public async Task A_malformed_header_line_is_dropped_rather_than_failing_the_check()
    {
        // A typo in an optional field must not take a monitor down: the bad lines are skipped and the
        // good one still arrives.
        var result = await EchoAsync("\"x-good-header\": \"kept\"", c => c.Headers = _protector.Protect(
            "this-line-has-no-colon\n# a comment\n\nX-Good-Header: kept"));

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_HEAD_request_is_Up_and_carries_no_body()
    {
        var result = await GetAsync("/ok", c => c.Method = "HEAD");

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal("200", result.StatusCode);
    }

    // ── timing and cancellation ────────────────────────────────────────────────────────────────

    [E2EFact]
    public async Task A_slow_response_is_Up_and_its_time_is_measured()
    {
        // The fixture sleeps BEFORE the response line, deliberately: the checker measures to the
        // headers (ResponseHeadersRead), so a fixture that slept afterwards would report a fast probe
        // and every Degraded scenario in Tier 2 would silently stop testing anything.
        var result = await GetAsync("/slow?ms=1500", cancelAfter: TimeSpan.FromSeconds(20));

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.True(result.ResponseTimeMs >= 1400,
            $"expected roughly 1500 ms, measured {result.ResponseTimeMs:0} ms — is the fixture sleeping after the headers?");
    }

    [E2EFact]
    public async Task A_response_slower_than_the_probe_allows_is_cancelled()
    {
        // Not Down("Timeout"). HttpChecker rethrows the cancellation so the runner can tell a per-check
        // timeout from application shutdown; the timeout message is produced one layer up.
        var ctx = Probe.Context(MonitorType.Http,
            new HttpMonitorConfig { Url = $"{Base}/slow?ms=10000" });

        await Probe.AssertCancelledAsync(_http, ctx, TimeSpan.FromSeconds(2));
    }

    // ── failure to reach the target at all ─────────────────────────────────────────────────────

    [E2EFact]
    public async Task A_closed_port_is_a_soft_Down()
    {
        var result = await GetAsync($"http://{Targets.Host}:{Targets.TcpRefusedPort}/ok");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task A_blackholed_port_hangs_until_the_probe_is_cancelled()
    {
        // The pooled probe clients carry HttpClient.Timeout of 100 seconds, so without the token this
        // would sit there for over a minute rather than fail.
        var ctx = Probe.Context(MonitorType.Http,
            new HttpMonitorConfig { Url = $"http://{Targets.Host}:{Targets.TcpBlackholePort}/ok" });

        await Probe.AssertCancelledAsync(_http, ctx, TimeSpan.FromSeconds(3));
    }

    [E2ETheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unconfigured_monitor_says_so_instead_of_probing(string url)
    {
        var result = await ProbeAsync(new HttpMonitorConfig { Url = url });

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("No URL configured", result.Message);
        Assert.Null(result.ResponseTimeMs);
    }

    [E2EFact]
    public async Task Break_and_restore_moves_the_endpoint_Down_and_back()
    {
        var before = await GetAsync("/toggle");
        Assert.Equal(CheckStatus.Up, before.Status);

        using (var broken = TargetControl.Break(Target.Http))
        {
            var during = await GetAsync("/toggle");
            Assert.Equal(CheckStatus.Down, during.Status);
            Assert.True(during.Hard, "a 503 from the server is a definitive answer and must be hard");
            Assert.Equal("503", during.StatusCode);

            broken.RestoreNow();

            var after = await GetAsync("/toggle");
            Assert.Equal(CheckStatus.Up, after.Status);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes <c>/echo</c> with <paramref name="keyword"/> as the monitor's Keyword, so the result is
    /// Up when the echoed request contains that string and Down when it does not.
    /// <para>
    /// The fixture renders the echo with <c>json.dumps(…, indent=2)</c> and lower-cases header names,
    /// so a keyword is written the way that output reads — <c>"x-api-key": "value"</c>, with the space
    /// after the colon. Keyword matching is case-insensitive, so only the punctuation has to be right.
    /// </para>
    /// </summary>
    private Task<CheckResult> EchoAsync(string keyword, Action<HttpMonitorConfig>? configure = null)
    {
        var cfg = new HttpMonitorConfig { Url = Base + "/echo" };
        configure?.Invoke(cfg);
        cfg.Keyword = keyword;
        return ProbeAsync(cfg);
    }
}

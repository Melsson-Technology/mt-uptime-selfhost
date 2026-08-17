using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Tests;

public class HttpCheckerTests
{
    // --- HttpMonitorConfig.IsStatusAccepted: pure parsing of "singles and ranges" ---

    [Theory]
    [InlineData("200-299", 200, true)]   // range, inclusive low
    [InlineData("200-299", 299, true)]   // range, inclusive high
    [InlineData("200-299", 300, false)]  // just past the range
    [InlineData("200-299", 199, false)]  // just below the range
    [InlineData("200-299,301", 301, true)]   // single appended to a range
    [InlineData("200-299,301", 302, false)]
    [InlineData("200,201,204", 204, true)]   // list of singles
    [InlineData("200,201,204", 202, false)]
    [InlineData("200-204, 301", 301, true)]  // whitespace around a part is trimmed
    [InlineData("200-299,500-599", 503, true)] // a later range matches
    [InlineData("abc,200", 200, true)]       // an unparseable part is skipped, not fatal
    [InlineData("200-,201", 200, false)]     // half-open range accepts nothing...
    [InlineData("200-,201", 201, true)]      // ...but a valid single still counts
    [InlineData("", 200, false)]             // empty spec accepts nothing
    public void IsStatusAccepted_parses_singles_and_ranges(string spec, int code, bool expected)
        => Assert.Equal(expected, new HttpMonitorConfig { AcceptedStatusCodes = spec }.IsStatusAccepted(code));

    // --- HttpChecker.CheckAsync: status + keyword decision, over a stubbed transport ---

    [Fact]
    public async Task Empty_url_is_down_before_any_request()
    {
        var r = await RunAsync(new HttpMonitorConfig { Url = "" });
        Assert.Equal(CheckStatus.Down, r.Status);
        Assert.Contains("No URL", r.Message);
        Assert.False(r.Hard);
    }

    [Fact]
    public async Task Accepted_status_with_no_keyword_is_up()
    {
        var r = await RunAsync(Cfg(), HttpStatusCode.OK);
        Assert.Equal(CheckStatus.Up, r.Status);
        Assert.Equal("200", r.StatusCode);
    }

    [Fact]
    public async Task An_unaccepted_status_is_a_hard_down()
    {
        var r = await RunAsync(Cfg(), HttpStatusCode.InternalServerError);
        Assert.Equal(CheckStatus.Down, r.Status);
        Assert.True(r.Hard);              // a received-but-rejected status is a definitive negative
        Assert.Equal("500", r.StatusCode);
        Assert.Contains("500", r.Message);
    }

    [Fact]
    public async Task A_custom_accepted_range_can_treat_500_as_up()
    {
        var r = await RunAsync(Cfg(accepted: "200-599"), HttpStatusCode.InternalServerError);
        Assert.Equal(CheckStatus.Up, r.Status);
    }

    [Fact]
    public async Task Keyword_present_is_up_and_absent_is_a_soft_down()
    {
        var cfg = Cfg(keyword: "healthy");
        Assert.Equal(CheckStatus.Up, (await RunAsync(cfg, HttpStatusCode.OK, "all healthy here")).Status);

        var miss = await RunAsync(cfg, HttpStatusCode.OK, "nothing to see");
        Assert.Equal(CheckStatus.Down, miss.Status);
        Assert.False(miss.Hard);         // a body mismatch is transient-ish -> keep the retry cushion
        Assert.Contains("not found", miss.Message);
    }

    [Fact]
    public async Task Keyword_match_is_case_insensitive()
    {
        var r = await RunAsync(Cfg(keyword: "HEALTHY"), HttpStatusCode.OK, "we are Healthy");
        Assert.Equal(CheckStatus.Up, r.Status);
    }

    [Fact]
    public async Task An_inverted_keyword_fails_when_present_and_passes_when_absent()
    {
        var cfg = Cfg(keyword: "error");
        cfg.KeywordInverted = true;

        var bad = await RunAsync(cfg, HttpStatusCode.OK, "fatal error occurred");
        Assert.Equal(CheckStatus.Down, bad.Status);
        Assert.Contains("present", bad.Message);

        var good = await RunAsync(cfg, HttpStatusCode.OK, "everything nominal");
        Assert.Equal(CheckStatus.Up, good.Status);
    }

    // --- Authentication: what actually goes on the wire ---

    [Fact]
    public async Task Basic_auth_sends_the_base64_of_user_colon_password()
    {
        var cfg = Cfg();
        cfg.AuthMode = HttpAuthMode.Basic;
        cfg.AuthUsername = "probe";
        cfg.AuthSecret = "s3cret";   // PassthroughProtector: ciphertext == plaintext

        var sent = await CaptureAsync(cfg);
        Assert.Equal("Basic", sent.Headers.Authorization!.Scheme);
        Assert.Equal("probe:s3cret", Encoding.UTF8.GetString(
            Convert.FromBase64String(sent.Headers.Authorization.Parameter!)));
    }

    [Fact]
    public async Task Bearer_auth_sends_the_token_unencoded()
    {
        var cfg = Cfg();
        cfg.AuthMode = HttpAuthMode.Bearer;
        cfg.AuthSecret = "tok_abc123";

        var sent = await CaptureAsync(cfg);
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal("tok_abc123", sent.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task No_auth_mode_sends_no_authorization_header()
        => Assert.Null((await CaptureAsync(Cfg())).Headers.Authorization);

    [Fact]
    public async Task A_credential_that_will_not_decrypt_is_a_hard_down_and_says_so()
    {
        var cfg = Cfg();
        cfg.AuthMode = HttpAuthMode.Bearer;
        cfg.AuthSecret = "whatever";

        // Stands in for a lost or mismatched Data Protection key ring.
        var checker = new HttpChecker(new StubHttpClientFactory(new StubHandler(HttpStatusCode.OK, "")), new FailingProtector());
        var ctx = new MonitorContext(1, "test", MonitorType.Http, TimeSpan.FromSeconds(5), JsonSerializer.Serialize(cfg));
        var r = await checker.CheckAsync(ctx, CancellationToken.None);

        Assert.Equal(CheckStatus.Down, r.Status);
        Assert.True(r.Hard);                        // retrying cannot bring the key ring back
        Assert.Contains("could not be decrypted", r.Message);
    }

    // --- Custom headers, body, User-Agent ---

    [Fact]
    public async Task Custom_headers_reach_the_request()
    {
        var cfg = Cfg();
        cfg.Headers = "X-API-Key: abc123\nAccept: application/json";

        var sent = await CaptureAsync(cfg);
        Assert.Equal("abc123", sent.Headers.GetValues("X-API-Key").Single());
        Assert.Equal("application/json", sent.Headers.GetValues("Accept").Single());
    }

    [Fact]
    public async Task A_custom_header_overrides_the_auth_mode_rather_than_appending()
    {
        var cfg = Cfg();
        cfg.AuthMode = HttpAuthMode.Bearer;
        cfg.AuthSecret = "from-the-field";
        cfg.Headers = "Authorization: Bearer from-the-header";

        // Headers are applied last on purpose: they are the escape hatch for schemes we do not model.
        var sent = await CaptureAsync(cfg);
        Assert.Equal("Bearer from-the-header", sent.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task A_body_is_sent_with_the_configured_content_type()
    {
        var cfg = Cfg();
        cfg.Method = "POST";
        cfg.Body = """{"probe":true}""";
        cfg.ContentType = "application/json";

        var sent = await CaptureAsync(cfg);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"probe":true}""", await sent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_content_type_header_line_is_routed_to_the_body_and_wins()
    {
        var cfg = Cfg();
        cfg.Method = "POST";
        cfg.Body = "ping=1";
        cfg.ContentType = "application/json";
        cfg.Headers = "Content-Type: application/x-www-form-urlencoded";

        // .NET rejects content headers on the request collection, so this only works if they are routed.
        var sent = await CaptureAsync(cfg);
        Assert.Equal("application/x-www-form-urlencoded", sent.Content!.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task A_user_agent_override_replaces_the_default()
    {
        var cfg = Cfg();
        cfg.UserAgent = "Allowlisted-Probe/2.0";

        var sent = await CaptureAsync(cfg);
        Assert.Equal("Allowlisted-Probe/2.0", sent.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task A_malformed_method_is_a_down_result_rather_than_an_escaping_exception()
    {
        // The request is built inside the try for this reason: thrown here it would reach the scheduler.
        var cfg = Cfg();
        cfg.Method = "GET POST";

        var r = await RunAsync(cfg);
        Assert.Equal(CheckStatus.Down, r.Status);
    }

    // --- ParseHeaders: pure text handling ---

    [Fact]
    public void ParseHeaders_skips_blanks_comments_and_lines_without_a_colon()
    {
        var parsed = HttpMonitorConfig.ParseHeaders(
            "X-One: 1\n\n# a comment: not a header\nnonsense\n  X-Two :  spaced  \r\n: novalue")
            .ToList();

        Assert.Equal(2, parsed.Count);
        Assert.Equal(("X-One", "1"), parsed[0]);
        Assert.Equal(("X-Two", "spaced"), parsed[1]);   // name right-trimmed, value left-trimmed then trimmed
    }

    [Fact]
    public void ParseHeaders_keeps_a_colon_inside_the_value()
    {
        var parsed = HttpMonitorConfig.ParseHeaders("X-Trace: id=1; host=a:8080").Single();
        Assert.Equal("id=1; host=a:8080", parsed.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseHeaders_of_nothing_is_empty(string? text)
        => Assert.Empty(HttpMonitorConfig.ParseHeaders(text));

    // --- helpers ---

    private static HttpMonitorConfig Cfg(string accepted = "200-299", string? keyword = null)
        => new() { Url = "http://monitor.test", AcceptedStatusCodes = accepted, Keyword = keyword };

    private static async Task<CheckResult> RunAsync(
        HttpMonitorConfig cfg, HttpStatusCode status = HttpStatusCode.OK, string body = "")
    {
        var checker = new HttpChecker(new StubHttpClientFactory(new StubHandler(status, body)), new PassthroughProtector());
        var ctx = new MonitorContext(1, "test", MonitorType.Http, TimeSpan.FromSeconds(5), JsonSerializer.Serialize(cfg));
        return await checker.CheckAsync(ctx, CancellationToken.None);
    }

    /// <summary>Runs a check and hands back the request the checker actually built.</summary>
    private static async Task<HttpRequestMessage> CaptureAsync(HttpMonitorConfig cfg)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        var checker = new HttpChecker(new StubHttpClientFactory(handler), new PassthroughProtector());
        var ctx = new MonitorContext(1, "test", MonitorType.Http, TimeSpan.FromSeconds(5), JsonSerializer.Serialize(cfg));

        var r = await checker.CheckAsync(ctx, CancellationToken.None);
        Assert.Equal(CheckStatus.Up, r.Status);   // a failed check would make the assertions vacuous
        return Assert.IsType<HttpRequestMessage>(handler.LastRequest);
    }

    /// <summary>Returns one canned response for any request — no network involved.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        /// <summary>
        /// Buffered here because the checker disposes the request once the check returns, which
        /// disposes its content with it — reading it afterwards would otherwise throw.
        /// </summary>
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (request.Content is not null)
            {
                copy.Content = new StringContent(await request.Content.ReadAsStringAsync(ct));
                copy.Content.Headers.Clear();
                foreach (var h in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            LastRequest = copy;

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Identity "encryption" — keeps the tests about the checker, not Data Protection.</summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    /// <summary>Stands in for a lost or mismatched Data Protection key ring.</summary>
    private sealed class FailingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => throw new CryptographicException("bad key ring");
    }
}

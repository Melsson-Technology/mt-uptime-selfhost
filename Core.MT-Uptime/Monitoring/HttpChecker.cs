using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Monitoring;

/// <summary>Checks an HTTP/S endpoint: status-code range, optional keyword match, and response time.</summary>
public sealed class HttpChecker(IHttpClientFactory httpFactory, ISecretProtector protector) : IMonitorChecker
{
    public const string ClientDefault = "monitor";
    public const string ClientNoRedirect = "monitor-noredirect";
    public const string ClientInsecure = "monitor-insecure";
    public const string ClientInsecureNoRedirect = "monitor-insecure-noredirect";

    /// <summary>
    /// The pooled client for one pair of per-monitor toggles. <c>AddMonitoringEngine</c> registers the
    /// same four cases from this method, so the checker's choice and the container's registrations
    /// cannot drift apart.
    /// <para>
    /// Both toggles are properties of the primary handler rather than of an individual request — there
    /// is no way to turn redirect-following off for a single send — so two independent axes have to
    /// exist as four clients. It is written as one total mapping because the previous form tested
    /// <c>IgnoreTlsErrors</c> first and stopped there: ticking "ignore TLS certificate errors" quietly
    /// turned redirect-following back on, so a monitor whose operator had explicitly unticked "follow
    /// redirects" would follow a 302 to a login page, report Up, and keep the outage invisible. The two
    /// checkboxes are independent in the editor and have to stay independent here.
    /// </para>
    /// </summary>
    public static string ClientNameFor(bool ignoreTlsErrors, bool followRedirects) =>
        (ignoreTlsErrors, followRedirects) switch
        {
            (false, true) => ClientDefault,
            (false, false) => ClientNoRedirect,
            (true, true) => ClientInsecure,
            (true, false) => ClientInsecureNoRedirect,
        };

    /// <summary>
    /// Sent on every HTTP probe unless the monitor overrides it. A request with no User-Agent looks like
    /// an anonymous scraper, and many sites/WAFs answer those with 403 — so identify ourselves like a
    /// well-behaved bot.
    /// </summary>
    public const string UserAgent = "MT-Uptime/1.0 (+https://melssontechnology.com)";

    public MonitorType Type => MonitorType.Http;

    public async Task<CheckResult> CheckAsync(MonitorContext ctx, CancellationToken ct)
    {
        var cfg = Deserialize(ctx.ConfigJson);
        if (string.IsNullOrWhiteSpace(cfg.Url))
            return CheckResult.Down("No URL configured");

        // Per-monitor toggles map to pre-registered handlers (see AddMonitoringEngine).
        var client = httpFactory.CreateClient(ClientNameFor(cfg.IgnoreTlsErrors, cfg.FollowRedirects));

        var sw = Stopwatch.StartNew();
        HttpRequestMessage? req = null;
        try
        {
            // Building the request is inside the try on purpose: an unparseable method or URL, or a
            // credential that will not decrypt, has to come back as a Down result. Thrown out of here it
            // would escape into the scheduler instead of being reported against the monitor.
            req = BuildRequest(cfg);

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;

            string? body = null;
            if (!string.IsNullOrEmpty(cfg.Keyword))
                body = await ReadBodyPrefixAsync(resp, ct);

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            // A received-but-unaccepted status is a definitive negative answer from the server —
            // mark it "hard" so the engine confirms Down at once instead of waiting out retries.
            if (!cfg.IsStatusAccepted(code))
                return CheckResult.Down($"Unexpected status {code}", ms, code.ToString(), hard: true);

            if (!string.IsNullOrEmpty(cfg.Keyword))
            {
                var present = body!.Contains(cfg.Keyword, StringComparison.OrdinalIgnoreCase);
                if (present == cfg.KeywordInverted)
                    return CheckResult.Down(
                        cfg.KeywordInverted ? $"Keyword \"{cfg.Keyword}\" present" : $"Keyword \"{cfg.Keyword}\" not found",
                        ms, code.ToString());
            }

            return CheckResult.Up(ms, code.ToString());
        }
        catch (OperationCanceledException)
        {
            throw; // let the runner distinguish a per-check timeout from app shutdown
        }
        catch (SecretUnreadableException ex)
        {
            // Retrying cannot help — the key ring is gone — so confirm Down at once rather than burning
            // the retry cushion. Reported distinctly because the alternative (send the request without
            // the credential) returns 401 and reads as the target's fault when it is ours.
            sw.Stop();
            return CheckResult.Down(ex.Message, sw.Elapsed.TotalMilliseconds, hard: true);
        }
        catch (Exception ex)
        {
            // ProbeFailure.Describe, not ex.Message. A rejected server certificate arrives here as
            // "The SSL connection could not be established, see inner exception." — a sentence with no
            // information in it. The reason is one level down and used to be discarded.
            sw.Stop();
            return CheckResult.Down(ProbeFailure.Describe(ex), sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            req?.Dispose();
        }
    }

    private HttpRequestMessage BuildRequest(HttpMonitorConfig cfg)
    {
        var method = string.IsNullOrWhiteSpace(cfg.Method) ? "GET" : cfg.Method.Trim().ToUpperInvariant();
        var req = new HttpRequestMessage(new HttpMethod(method), cfg.Url);

        // Setting User-Agent on the request suppresses the pooled client's default, which is only
        // applied to headers the request does not already carry.
        if (!string.IsNullOrWhiteSpace(cfg.UserAgent))
            req.Headers.TryAddWithoutValidation("User-Agent", cfg.UserAgent.Trim());

        switch (cfg.AuthMode)
        {
            case HttpAuthMode.Basic:
                var pair = $"{cfg.AuthUsername}:{Reveal(cfg.AuthSecret)}";
                req.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
                break;

            case HttpAuthMode.Bearer:
                var token = Reveal(cfg.AuthSecret);
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
        }

        // Content before headers, so an explicit "Content-Type:" line can override ContentType below.
        if (!string.IsNullOrEmpty(cfg.Body))
        {
            var mediaType = string.IsNullOrWhiteSpace(cfg.ContentType) ? "application/json" : cfg.ContentType.Trim();
            req.Content = new StringContent(cfg.Body, Encoding.UTF8, mediaType);
        }

        // Applied last, so a custom line wins over anything set above. This is the escape hatch for
        // schemes we do not model (signed tokens, X-API-Key, a WAF's expected header).
        foreach (var (name, value) in HttpMonitorConfig.ParseHeaders(Reveal(cfg.Headers)))
            ApplyHeader(req, name, value);

        return req;
    }

    /// <summary>
    /// Sets one header, choosing the request or content collection for it. .NET splits the two and
    /// rejects a content header (Content-Type, Content-Length…) added to the request, so a failed add is
    /// a routing signal rather than an error.
    /// </summary>
    private static void ApplyHeader(HttpRequestMessage req, string name, string value)
    {
        // Remove before adding, so a custom line beats whatever was set above it (Authorization,
        // User-Agent) rather than appending a second value to a header that allows several.
        try
        {
            req.Headers.Remove(name);
            if (req.Headers.TryAddWithoutValidation(name, value)) return;
        }
        catch (FormatException) { return; }   // not a valid header name at all — skip the line
        catch (InvalidOperationException) { } // a content header; handled below

        if (req.Content is null) return;
        try
        {
            req.Content.Headers.Remove(name);
            req.Content.Headers.TryAddWithoutValidation(name, value);
        }
        catch (FormatException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Decrypts a stored secret, or throws <see cref="SecretUnreadableException"/>.</summary>
    private string? Reveal(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        try { return protector.Unprotect(cipher); }
        catch (Exception ex)
        {
            throw new SecretUnreadableException(
                "A stored credential for this monitor could not be decrypted — the Data Protection key " +
                "ring is missing or does not match the database. See deploy/README-deploy.md.", ex);
        }
    }

    /// <summary>
    /// How much of a response body is read to search for the keyword. Generous for a health endpoint or
    /// a status page, and bounded — which is the point.
    /// </summary>
    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> of the response body.
    /// <para>
    /// This used to be <c>ReadAsStringAsync</c>, which reads until the response ends. The response comes
    /// from the monitored host — a party outside the trust boundary — and the request is sent with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so a target answering with
    /// <c>Transfer-Encoding: chunked</c> and never stopping could stream for the whole check timeout
    /// (30 s by default) and take the process out with an OutOfMemoryException. On a 1 GB box that is
    /// every monitor stopping, the queued heartbeats and notifications lost with the process, and a
    /// restart straight back into the same monitor. Note <c>MaxResponseContentBufferSize</c> is inert
    /// under ResponseHeadersRead, so the limit has to be applied here.
    /// </para>
    /// <para>
    /// A keyword split across the cut-off is treated as absent. That is the safe direction: the check
    /// reports Down and an operator investigates, rather than a truncated read silently reporting Up.
    /// </para>
    /// </summary>
    private static async Task<string> ReadBodyPrefixAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[MaxBodyBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }

        // Decoded with the charset the response declares, falling back to UTF-8. Truncating at a byte
        // boundary can leave a partial multi-byte sequence at the tail; GetString substitutes a
        // replacement character rather than throwing, which is fine for a substring search.
        var encoding = Encoding.UTF8;
        var charset = resp.Content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* unknown charset — UTF-8 is the better guess than failing */ }
        }

        return encoding.GetString(buffer, 0, total);
    }

    private static HttpMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<HttpMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }
}

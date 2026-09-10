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

        // Collects the DNS / connect / TLS legs from inside the pooled handler's callbacks. Cleared in
        // the finally below so a later probe on a recycled thread cannot write into this one.
        var timings = ProbeTimings.Begin();

        try
        {
            // Building the request is inside the try on purpose: an unparseable method or URL, or a
            // credential that will not decrypt, has to come back as a Down result. Thrown out of here it
            // would escape into the scheduler instead of being reported against the monitor.
            req = BuildRequest(cfg);

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;

            // ResponseHeadersRead means SendAsync returns as soon as the headers land, so this really is
            // time-to-first-byte rather than time-to-complete-response.
            var ttfbMs = sw.Elapsed.TotalMilliseconds;

            var accepted = cfg.IsStatusAccepted(code);

            // The body is read when a keyword needs matching, and otherwise only when the check has
            // already failed — where a CDN or origin error page usually states the cause in words.
            string? body = null;
            if (!string.IsNullOrEmpty(cfg.Keyword) || !accepted)
                body = await ReadBodyPrefixAsync(resp, ct);

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            // A received-but-unaccepted status is a definitive negative answer from the server —
            // mark it "hard" so the engine confirms Down at once instead of waiting out retries.
            if (!accepted)
                return CheckResult.Down($"Unexpected status {code}", ms, code.ToString(), hard: true)
                    with { Diagnostics = Diagnose(resp, body, timings, ms, ttfbMs, cfg.Url) };

            if (!string.IsNullOrEmpty(cfg.Keyword))
            {
                var present = body!.Contains(cfg.Keyword, StringComparison.OrdinalIgnoreCase);
                if (present == cfg.KeywordInverted)
                    return CheckResult.Down(
                        cfg.KeywordInverted ? $"Keyword \"{cfg.Keyword}\" present" : $"Keyword \"{cfg.Keyword}\" not found",
                        ms, code.ToString())
                        with { Diagnostics = Diagnose(resp, body, timings, ms, ttfbMs, cfg.Url) };
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
            sw.Stop();
            // The exception overload, not ex.Message: for a TLS failure the outer message is only an
            // instruction to read the inner one, and for a connection failure the inner SocketException
            // is what separates "refused" from "timed out". See ProbeFailure.Describe.
            //
            // The timings are still worth keeping: a connection that failed after 20 s in the TCP leg
            // and one that failed instantly at DNS are the same message and completely different faults.
            return CheckResult.Down(ex, sw.Elapsed.TotalMilliseconds)
                with { Diagnostics = new CheckDiagnostics { Timings = timings.ToBreakdown(sw.Elapsed.TotalMilliseconds, null) } };
        }
        finally
        {
            ProbeTimings.End();
            req?.Dispose();
        }
    }

    /// <summary>
    /// Assembles the evidence for a failed probe. Never throws: diagnostics are a bonus attached to a
    /// result that is already decided, and losing them must not turn a reportable failure into an
    /// exception escaping the checker.
    /// </summary>
    private static CheckDiagnostics? Diagnose(
        HttpResponseMessage resp, string? body, ProbeTimings timings, double totalMs, double ttfbMs,
        string? requestedUrl)
    {
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in CheckDiagnostics.InterestingHeaders)
            {
                // Response and content headers are separate collections in .NET; a caller asking for
                // "location" must not silently miss a "content-type".
                if (resp.Headers.TryGetValues(name, out var values) ||
                    resp.Content.Headers.TryGetValues(name, out values))
                {
                    headers[name] = Clean(string.Join(", ", values), 200)!;
                }
            }

            // Only when redirects actually moved us. Echoing the configured URL back at an operator who
            // just read it in the same alert is noise, but a monitor that silently followed a redirect
            // to a login page and called it healthy is a classic invisible outage — worth the line.
            // Compared as Uris, not strings: "http://example.com" and "http://example.com/" are the
            // same place, and Uri normalises the configured form the moment the request is built — so a
            // string comparison reports a redirect on literally every failing check.
            var landed = resp.RequestMessage?.RequestUri;
            var moved = landed is not null
                && (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out var asked) || landed != asked);
            var finalUrl = moved ? landed!.ToString() : null;

            return new CheckDiagnostics
            {
                Timings = timings.ToBreakdown(totalMs, ttfbMs),
                Headers = headers,
                BodySnippet = Summarize(body),
                FinalUrl = finalUrl,
                HttpVersion = resp.Version.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reduces an error page to the sentence it is trying to say: tags dropped, whitespace collapsed,
    /// then clipped. A Cloudflare 521 page is several kilobytes of markup wrapped around one useful line.
    /// </summary>
    private static string? Summarize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var text = new StringBuilder(Math.Min(body.Length, CheckDiagnostics.MaxBodySnippet * 4));
        var tagName = new StringBuilder(8);

        var inTag = false;
        var readingTagName = false;
        // The contents of <style> and <script> are text nodes, not markup, so tag-stripping alone
        // leaves them in. Nearly every error page carries inline CSS, and a snippet budget spent on
        // "body{font-family:sans-serif}" is a budget not spent on what the server was trying to say.
        var skipDepth = 0;

        foreach (var ch in body)
        {
            if (ch == '<')
            {
                inTag = true;
                readingTagName = true;
                tagName.Clear();
                continue;
            }

            if (ch == '>')
            {
                inTag = false;
                readingTagName = false;

                var name = tagName.ToString();
                if (name.StartsWith('/'))
                {
                    if (IsHiddenElement(name[1..]) && skipDepth > 0) skipDepth--;
                }
                else if (IsHiddenElement(name))
                {
                    skipDepth++;
                }

                if (skipDepth == 0) text.Append(' ');
                continue;
            }

            if (inTag)
            {
                // The name ends at the first whitespace; everything after it is attributes.
                if (readingTagName)
                {
                    if (char.IsWhiteSpace(ch)) readingTagName = false;
                    else if (tagName.Length < 8) tagName.Append(ch);
                }
                continue;
            }

            if (skipDepth == 0) text.Append(ch);

            // Enough raw characters to survive whitespace collapsing without walking a 256 KB body.
            if (text.Length > CheckDiagnostics.MaxBodySnippet * 4) break;
        }

        return Clean(text.ToString(), CheckDiagnostics.MaxBodySnippet);
    }

    /// <summary>Elements whose text content is code rather than prose.</summary>
    private static bool IsHiddenElement(string name)
        => name.Equals("style", StringComparison.OrdinalIgnoreCase)
        || name.Equals("script", StringComparison.OrdinalIgnoreCase)
        || name.Equals("head", StringComparison.OrdinalIgnoreCase);

    /// <summary>Collapses all whitespace runs to single spaces and clips to <paramref name="max"/>.</summary>
    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var sb = new StringBuilder(Math.Min(value.Length, max + 1));
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            var isSpace = char.IsWhiteSpace(ch) || char.IsControl(ch);
            if (isSpace)
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }

            if (sb.Length >= max) break;
        }

        var cleaned = sb.ToString().TrimEnd();
        return cleaned.Length == 0 ? null : cleaned;
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

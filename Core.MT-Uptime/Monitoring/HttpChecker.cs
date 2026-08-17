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
        var clientName = cfg.IgnoreTlsErrors ? ClientInsecure
                       : cfg.FollowRedirects ? ClientDefault
                       : ClientNoRedirect;
        var client = httpFactory.CreateClient(clientName);

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
                body = await resp.Content.ReadAsStringAsync(ct);

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
            sw.Stop();
            return CheckResult.Down(ex.Message, sw.Elapsed.TotalMilliseconds);
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

    private static HttpMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<HttpMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }
}

/// <summary>Thrown when a monitor's stored credential cannot be decrypted with the current key ring.</summary>
public sealed class SecretUnreadableException(string message, Exception inner) : Exception(message, inner);

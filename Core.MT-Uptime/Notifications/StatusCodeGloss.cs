namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Turns an HTTP status code into the sentence an operator would otherwise have to go and look up.
/// <para>
/// <b>Why this earns its place in an alert.</b> The alert that prompted this said only
/// <c>Unexpected status 521</c>. 521 is not a standard HTTP code — it is Cloudflare's, and it means the
/// edge is healthy and answering while the <i>origin refused the connection</i>. That is a completely
/// different call at 00:25 from "the website is down": the DNS, the CDN and the TLS termination are all
/// fine, and the thing to go and restart is the origin. The reader cannot act on the number; they can
/// act on the sentence.
/// </para>
/// <para>
/// Deliberately narrow. Glossing <c>404</c> teaches nobody anything and pads every alert, which trains
/// people to skip the detail lines — the same argument that keeps
/// <see cref="Incidents.AlertEnrichment.ExpiryThreshold"/> from mentioning healthy certificates. Only
/// codes that are genuinely obscure, genuinely ambiguous, or point somewhere other than where the
/// reader's first instinct would send them are listed.
/// </para>
/// </summary>
public static class StatusCodeGloss
{
    /// <summary>
    /// The 5xx range Cloudflare defines for itself. Every one of these means the same important thing —
    /// <i>Cloudflare is up and your origin is the problem</i> — so they share a prefix, and the specific
    /// clause says which leg failed.
    /// </summary>
    private const string CloudflarePrefix = "Cloudflare is answering, but ";

    private static readonly Dictionary<int, string> Glosses = new()
    {
        // Standard codes worth expanding, because each one sends people to the wrong place first.
        [401] = "the target rejected our credentials — a monitor secret may have expired.",
        [403] = "the target refused the request. Often a WAF or bot rule rather than the application.",
        [429] = "the target is rate-limiting us. Consider a slower check interval for this monitor.",
        [502] = "an upstream server returned an invalid response to the proxy in front of it.",
        [503] = "the service is up but refusing requests — typically overloaded, or in maintenance.",
        [504] = "a proxy gave up waiting for the server behind it.",

        // Cloudflare's own range. Almost nobody knows these by heart, and they are precise about which
        // hop failed, which makes them the most actionable codes in the whole list.
        [520] = CloudflarePrefix + "the origin returned an empty or malformed response.",
        [521] = CloudflarePrefix + "the origin refused the connection — the web server is down or blocking Cloudflare's IPs.",
        [522] = CloudflarePrefix + "the connection to the origin timed out — the origin is unreachable or overloaded.",
        [523] = CloudflarePrefix + "the origin is unreachable — usually a DNS or routing fault behind the edge.",
        [524] = CloudflarePrefix + "the origin accepted the connection then failed to answer in time.",
        [525] = CloudflarePrefix + "the TLS handshake with the origin failed — check the origin's certificate and cipher config.",
        [526] = CloudflarePrefix + "the origin's certificate is invalid or expired.",
        [530] = CloudflarePrefix + "it could not serve the request at all — check the accompanying Cloudflare 1xxx error.",
    };

    /// <summary>
    /// The explanation for a status code, or null when there is nothing useful to add.
    /// <para>
    /// Takes the free-form string that <see cref="Monitoring.CheckResult.StatusCode"/> and
    /// <see cref="Domain.Heartbeat.StatusCode"/> carry, because that field has to hold every monitor
    /// type's notion of a code — a TLS check stores <c>"expired"</c> there. Anything non-numeric simply
    /// has no gloss.
    /// </para>
    /// </summary>
    public static string? For(string? statusCode)
        => int.TryParse(statusCode, out var code) ? For(code) : null;

    /// <summary>The explanation for a status code, or null when there is nothing useful to add.</summary>
    public static string? For(int statusCode)
        => Glosses.GetValueOrDefault(statusCode);
}

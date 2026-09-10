using System.Text.Json;
using System.Text.Json.Serialization;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Where the time went on one probe, in milliseconds.
/// <para>
/// This is the field that answers the question the prompting alert could not: a 21,556 ms response
/// against a 45–75 ms baseline says <i>something</i> hung, but not <i>what</i>. DNS slow, TCP connect
/// refused-then-retried, TLS handshake stalling and an origin sitting on the request are four different
/// faults with four different owners, and they are indistinguishable from the total alone.
/// </para>
/// <para>
/// <b>Every leg is nullable, and that is load-bearing.</b> HTTP connections are pooled, so a probe that
/// reuses a warm connection performs no DNS lookup, no TCP handshake and no TLS negotiation. Reporting
/// those as <c>0 ms</c> would be a lie that reads as "instant" rather than "did not happen" — see
/// <see cref="ConnectionReused"/>.
/// </para>
/// </summary>
public sealed record ProbeTimingBreakdown(
    double? DnsMs,
    double? ConnectMs,
    double? TlsMs,
    double? TimeToFirstByteMs,
    double TotalMs)
{
    /// <summary>
    /// True when this probe rode an already-open connection, so the setup legs above are absent by
    /// nature rather than unmeasured. Worth stating in the alert: a slow request on a reused connection
    /// puts the blame squarely on the server, with the whole network setup path ruled out.
    /// <para>
    /// <b>This claim is only safe because a failed leg still records its elapsed time.</b> The legs are
    /// written from <c>ConnectCallback</c>, which a pooled connection never enters — but so does a probe
    /// whose DNS lookup threw. If resolution failure left the DNS leg null, this would report a ruled-out
    /// network path on the very probe where the network was the fault. See <c>TimedConnectAsync</c>,
    /// where both legs are recorded in a <c>finally</c>; <c>ProbeTimingsTests</c> pins the invariant.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool ConnectionReused => DnsMs is null && ConnectMs is null && TlsMs is null;
}

/// <summary>
/// The evidence a failing HTTP probe leaves behind, beyond its status code.
/// <para>
/// Captured only on the failure path. On the happy path — the overwhelming majority of checks — none of
/// this is gathered, nothing is allocated for it, and the heartbeat's column stays NULL.
/// </para>
/// <para>
/// <b>Everything here is target-controlled text.</b> Headers, body and final URL all come from the
/// monitored host, which is outside the trust boundary and is frequently the very host whose operator
/// would rather not be alerted. It is bounded on capture (an allowlist for headers,
/// <see cref="MaxBodySnippet"/> for the body) for the same reason
/// <see cref="CheckResult.MaxMessageLength"/> exists: a target able to inflate this can push the
/// outbound alert past a channel's payload limit and so suppress the notification about its own outage.
/// </para>
/// </summary>
public sealed record CheckDiagnostics
{
    /// <summary>
    /// How much of a failing response body to quote. Long enough for a CDN error page to state its
    /// case and name its reference id, short enough to sit inside an alert nobody scrolls.
    /// </summary>
    public const int MaxBodySnippet = 300;

    /// <summary>
    /// Response headers worth keeping, lower-cased. An allowlist rather than a copy of everything:
    /// unbounded header capture is both a size problem and a disclosure one, since a response can carry
    /// session cookies and internal routing detail we have no business persisting or emailing.
    /// <para>
    /// <c>cf-ray</c> is the highest-value entry for the case that prompted this. It is Cloudflare's
    /// per-request id, it encodes the edge datacenter that served the request, and it is the first thing
    /// Cloudflare support asks for. Without it, a 521 is an unfalsifiable claim; with it, the failure
    /// can be looked up.
    /// </para>
    /// </summary>
    public static readonly string[] InterestingHeaders =
    [
        "server",         // names the fronting software — "cloudflare" changes the whole diagnosis
        "cf-ray",         // Cloudflare request id + edge datacenter
        "cf-cache-status",
        "via",            // any other proxy in the path
        "x-cache",        // CDN hit/miss
        "retry-after",    // the target telling us when to come back (429/503)
        "location",       // where a redirect was pointing when it failed
        "x-request-id",   // many origins' own correlation id
    ];

    /// <summary>Where the time went. Null when the probe failed before any of it could be measured.</summary>
    public ProbeTimingBreakdown? Timings { get; init; }

    /// <summary>Allowlisted response headers, lower-cased keys. Empty rather than null when none matched.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The first <see cref="MaxBodySnippet"/> characters of the failing response, tags stripped and
    /// whitespace collapsed. A Cloudflare 521 page says what happened in plain words; so do most
    /// origin error pages, and none of it was reaching the operator.
    /// </summary>
    public string? BodySnippet { get; init; }

    /// <summary>
    /// Where the request actually ended up, when that differs from where it was aimed. A monitor that
    /// silently follows a redirect to a login page is a classic invisible outage.
    /// </summary>
    public string? FinalUrl { get; init; }

    /// <summary>The negotiated HTTP version, e.g. "1.1" or "2".</summary>
    public string? HttpVersion { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes for the <c>Heartbeat.Diagnostics</c> column, or null when there is nothing to say.</summary>
    public string? ToJson()
        => Timings is null && Headers.Count == 0 && BodySnippet is null && FinalUrl is null
            ? null
            : JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads the column back. Returns null rather than throwing on anything unparseable: this is
    /// display-only context, and a row written by a different build must never break the incident page.
    /// </summary>
    public static CheckDiagnostics? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CheckDiagnostics>(json, Json); }
        catch (JsonException) { return null; }
    }
}

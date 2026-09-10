using System.Globalization;
using System.Net;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// How bad an alert is, in terms every channel can express. Channels map this to their own vocabulary —
/// a Slack emoji, a Discord embed colour, an ntfy priority, a PagerDuty severity.
/// </summary>
public enum AlertSeverity
{
    /// <summary>Recovered. Green everywhere.</summary>
    Good,

    /// <summary>Still answering, but not well. Amber — never red, or a slowdown reads as an outage.</summary>
    Warning,

    /// <summary>Down. Red, and loud.</summary>
    Bad,

    /// <summary>Anything else, including a kind this build does not recognise.</summary>
    Info,
}

/// <summary>
/// How much of the captured evidence a channel can carry.
/// <para>
/// The split exists because the channels are not alike. Email, Slack and Teams have room for a timing
/// breakdown and a quoted error page; ntfy, Telegram and Gotify are push notifications read on a lock
/// screen, and several enforce payload limits that an oversized body silently fails against — the
/// failure mode a monitoring system least wants, since it drops the alert about an outage.
/// </para>
/// </summary>
public enum AlertVerbosity
{
    /// <summary>The full evidence block inline. For channels with room.</summary>
    Rich,

    /// <summary>Diagnosis and context only, with a link to the rest. For push channels.</summary>
    Terse,
}

/// <summary>Shared formatting so every channel renders a consistent subject/message from an event.</summary>
public static class NotificationRenderer
{
    /// <summary>
    /// The single place a <see cref="NotifyKind"/> becomes a severity.
    /// <para>
    /// Every channel needs some per-kind vocabulary, and each one written as its own switch over
    /// <c>NotifyKind</c> is a switch with a fallback arm that goes silently wrong the day a kind is
    /// added — which is exactly how <c>Degraded</c> shipped posting Slack's information icon. Routing
    /// them all through here means a new kind is one edit in one place, and the channels switch over
    /// this small, stable enum instead.
    /// </para>
    /// </summary>
    public static AlertSeverity SeverityOf(NotifyKind kind) => kind switch
    {
        NotifyKind.Up => AlertSeverity.Good,
        NotifyKind.Down or NotifyKind.ResendDown => AlertSeverity.Bad,
        NotifyKind.Degraded => AlertSeverity.Warning,
        _ => AlertSeverity.Info,
    };

    /// <summary>
    /// The single place a channel becomes a verbosity, for the same reason
    /// <see cref="SeverityOf"/> is the single place a kind becomes a severity: written as a switch per
    /// channel, each with a fallback arm, this is precisely the shape that goes silently wrong the day a
    /// channel is added — which is how <c>Degraded</c> shipped as Slack's information icon.
    /// <para>
    /// Every arm is named explicitly and there is no <c>_ =&gt;</c> default, so adding a channel type is a
    /// compile-time prompt to decide rather than a silent inheritance of someone else's answer.
    /// <c>Every_channel_type_declares_a_verbosity</c> pins it.
    /// </para>
    /// </summary>
    //
    // CS8524 is disabled, CS8509 deliberately is not. They are different questions: CS8509 fires when a
    // *named* enum member has no arm, which is the compile-time prompt this switch exists to get, and
    // `-warnaserror` — the project standard — turns it into a build failure. CS8524 only says that some
    // value cast from an out-of-range int is unhandled, which is true of every exhaustive enum switch in
    // C# and cannot be fixed without a default arm that would swallow CS8509 along with it.
#pragma warning disable CS8524
    public static AlertVerbosity VerbosityFor(NotificationChannelType channel) => channel switch
    {
        // Room to spare, and read at a desk more often than not.
        NotificationChannelType.Email => AlertVerbosity.Rich,
        NotificationChannelType.Slack => AlertVerbosity.Rich,
        NotificationChannelType.Teams => AlertVerbosity.Rich,
        NotificationChannelType.Discord => AlertVerbosity.Rich,

        // Structured payloads: these do not render the plain-text body at all, they carry the same
        // fields as JSON. Rich is the honest answer — nothing is being held back from them.
        NotificationChannelType.Webhook => AlertVerbosity.Rich,
        NotificationChannelType.PagerDuty => AlertVerbosity.Rich,

        // Push notifications read on a lock screen, and ntfy and Telegram both enforce payload limits
        // that an oversized body fails against — which would drop the alert entirely.
        NotificationChannelType.Ntfy => AlertVerbosity.Terse,
        NotificationChannelType.Telegram => AlertVerbosity.Terse,
        NotificationChannelType.Gotify => AlertVerbosity.Terse,
    };
#pragma warning restore CS8524

    public static (string Tag, string Verb) Describe(NotifyKind kind) => kind switch
    {
        NotifyKind.Down => ("DOWN", "is DOWN"),
        NotifyKind.Up => ("UP", "has RECOVERED"),
        NotifyKind.ResendDown => ("STILL DOWN", "is STILL DOWN"),
        // Still answering, just slowly — worded so it is never mistaken for an outage at a glance.
        NotifyKind.Degraded => ("SLOW", "is responding SLOWLY"),
        _ => ("INFO", "changed state"),
    };

    /// <summary>
    /// Subject line. A correlated incident is named here rather than only in the body, because on a phone
    /// the subject is often all that is read — and "one of 20" is the single most useful thing to know
    /// before deciding whether to get out of bed.
    /// </summary>
    public static string Subject(NotificationEvent e)
    {
        var (tag, _) = Describe(e.Kind);
        var scope = e.Incident is { IsCorrelated: true } i ? $" (+{i.MonitorCount - 1} more)" : "";
        return $"[MT-Uptime] {tag}: {e.MonitorName}{scope}";
    }

    public static string PlainText(NotificationEvent e, AlertVerbosity verbosity)
    {
        var (_, verb) = Describe(e.Kind);
        var lines = new List<string> { $"{e.MonitorName} {verb}.", $"Time (UTC): {e.At:u}" };
        if (FormatResponseTime(e) is { } timing) lines.Add($"Response time: {timing}");
        if (!string.IsNullOrWhiteSpace(e.Message)) lines.Add($"Detail: {e.Message}");

        // The diagnosis goes to every channel, however small. It is one line and it is the line most
        // likely to change what the reader does — dropping it to save space would be the wrong economy.
        foreach (var line in DiagnosisLines(e)) lines.Add(line);
        foreach (var line in IncidentLines(e)) lines.Add(line);
        foreach (var line in EnrichmentLines(e)) lines.Add(line);

        if (verbosity is AlertVerbosity.Rich)
            foreach (var line in EvidenceLines(e)) lines.Add(line);
        else if (e.DetailsUrl is { Length: > 0 } url)
            lines.Add($"Full diagnostics: {url}");

        return string.Join('\n', lines);
    }

    /// <summary>Email is always rich — it is the channel with the most room and the least urgency.</summary>
    public static string Html(NotificationEvent e)
    {
        var (_, verb) = Describe(e.Kind);
        var name = WebUtility.HtmlEncode(e.MonitorName);
        var timing = FormatResponseTime(e) is { } t ? $"<p>Response time: {WebUtility.HtmlEncode(t)}</p>" : "";
        var detail = string.IsNullOrWhiteSpace(e.Message) ? "" : $"<p>Detail: {WebUtility.HtmlEncode(e.Message)}</p>";

        var extra = string.Concat(
            DiagnosisLines(e).Concat(IncidentLines(e)).Concat(EnrichmentLines(e)).Concat(EvidenceLines(e))
            .Select(l => $"<p>{WebUtility.HtmlEncode(l)}</p>"));

        return $"<p><strong>{name}</strong> {verb}.</p><p>Time (UTC): {e.At:u}</p>{timing}{detail}{extra}";
    }

    /// <summary>
    /// The correlation, in words. Emitted only when more than one monitor is involved: on a single-monitor
    /// outage, "incident #12 affecting 1 monitor" is noise dressed as information.
    /// </summary>
    private static IEnumerable<string> IncidentLines(NotificationEvent e)
    {
        if (e.Incident is not { IsCorrelated: true } i) yield break;

        var where = i.SharedInfrastructure is null ? "" : $" on {i.SharedInfrastructure}";
        yield return $"Part of incident #{i.Id}: {i.MonitorCount} monitors are affected{where}.";

        if (i.OtherAffectedMonitors.Count > 0)
        {
            // Capped: a 40-monitor host would otherwise produce an alert nobody reads to the end of.
            const int Show = 8;
            var names = string.Join(", ", i.OtherAffectedMonitors.Take(Show));
            var rest = i.OtherAffectedMonitors.Count - Show;
            yield return rest > 0
                ? $"Also affected: {names} and {rest} more."
                : $"Also affected: {names}.";
        }

        if (i.Acknowledged) yield return "This incident has been acknowledged.";
    }

    /// <summary>
    /// The plain-language reading of the status code, when there is one worth giving. Emitted directly
    /// under <c>Detail</c> because it is the line most likely to change what the reader does next — for
    /// a 521 it is the difference between "the site is down" and "the origin behind Cloudflare is down".
    /// </summary>
    private static IEnumerable<string> DiagnosisLines(NotificationEvent e)
    {
        if (StatusCodeGloss.For(e.StatusCode) is { } gloss)
            yield return $"What {e.StatusCode} means: {gloss}";
    }

    /// <summary>The "what broke" context. Each line is omitted when the underlying value is unknown.</summary>
    private static IEnumerable<string> EnrichmentLines(NotificationEvent e)
    {
        if (e.Enrichment is not { } x) yield break;

        // The last *good* response, labelled as such. Reporting "last response code" next to a Detail
        // line naming a different code reads as a contradiction — see AlertEnrichment.LastGoodStatusCode.
        if (FormatLastGood(x, e.At) is { } lastGood) yield return $"Last good response: {lastGood}";

        if (x.PreviousStateSince is { } since && since <= e.At)
            yield return $"Previous state held for {FormatDuration(e.At - since)}.";

        if (!string.IsNullOrWhiteSpace(x.ResolvedAddress)) yield return $"Resolved to: {x.ResolvedAddress}";

        if (x.RecentResponseTimesMs.Count > 0)
        {
            var series = string.Join(", ", x.RecentResponseTimesMs
                .Select(ms => string.Create(CultureInfo.InvariantCulture, $"{ms:N0}")));
            yield return $"Recent response times before this (ms, oldest first): {series}";
        }

        // Only present when near expiry — see AlertEnrichment.ExpiryThreshold.
        if (x.CertificateExpiresAt is { } expires)
        {
            var days = (int)Math.Floor((expires - e.At).TotalDays);
            yield return days < 0
                ? $"Certificate EXPIRED {Math.Abs(days)} day(s) ago ({expires:u})."
                : $"Certificate expires in {days} day(s) ({expires:u}).";
        }
    }

    /// <summary>
    /// What the probe actually saw: where the time went, who answered, and what they said. Rich channels
    /// only — this is the block that would push an ntfy or Telegram payload past its limit, and those
    /// channels get <see cref="NotificationEvent.DetailsUrl"/> instead.
    /// </summary>
    private static IEnumerable<string> EvidenceLines(NotificationEvent e)
    {
        if (e.Diagnostics is not { } d) yield break;

        if (d.Timings is { } t)
        {
            var legs = new List<string>();
            if (t.DnsMs is { } dns) legs.Add($"DNS {dns:N0} ms");
            if (t.ConnectMs is { } conn) legs.Add($"connect {conn:N0} ms");
            if (t.TlsMs is { } tls) legs.Add($"TLS {tls:N0} ms");
            if (t.TimeToFirstByteMs is { } ttfb) legs.Add($"first byte {ttfb:N0} ms");
            legs.Add($"total {t.TotalMs:N0} ms");

            // Saying so matters: it rules out the entire network setup path, which puts a slow response
            // squarely on the server rather than on anything between us and it.
            var reused = t.ConnectionReused ? " (connection reused)" : "";
            yield return $"Where the time went: {string.Join(", ", legs)}{reused}";
        }

        // cf-ray first when present — it is the one value Cloudflare support can act on.
        foreach (var (name, value) in d.Headers.OrderBy(h => h.Key == "cf-ray" ? 0 : 1).ThenBy(h => h.Key, StringComparer.Ordinal))
            yield return $"{name}: {value}";

        if (!string.IsNullOrWhiteSpace(d.FinalUrl)) yield return $"Final URL: {d.FinalUrl}";
        if (!string.IsNullOrWhiteSpace(d.BodySnippet)) yield return $"Response body: {d.BodySnippet}";

        if (e.DetailsUrl is { Length: > 0 } url) yield return $"Full diagnostics: {url}";
    }

    /// <summary>
    /// Response time for the alert body, or null when the probe recorded none. Carries the most weight on
    /// a Degraded alert — "responding slowly" is not actionable without the number behind it — but it is
    /// useful context on any transition.
    /// </summary>
    private static string? FormatResponseTime(NotificationEvent e)
        => e.ResponseTimeMs is { } ms
            ? string.Create(CultureInfo.InvariantCulture, $"{ms:N0} ms")
            : null;

    /// <summary>
    /// "200, 46 ms, 1m ago" — whichever of the three parts are known. Null when the monitor has no
    /// successful check on record at all, which is normal for one that has never worked.
    /// </summary>
    private static string? FormatLastGood(Incidents.AlertEnrichment x, DateTime now)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(x.LastGoodStatusCode)) parts.Add(x.LastGoodStatusCode);
        if (x.LastGoodResponseTimeMs is { } ms)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ms:N0} ms"));
        if (x.LastGoodAt is { } at && at <= now) parts.Add($"{FormatDuration(now - at)} ago");

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>
    /// A duration at the coarsest unit that still says something, because "4d 3h" is read at a glance on
    /// a lock screen and "363,847 seconds" is not. Never returns an empty string.
    /// </summary>
    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }
}

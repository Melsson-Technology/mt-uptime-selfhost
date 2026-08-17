using System.Globalization;
using System.Net;
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

    public static string PlainText(NotificationEvent e)
    {
        var (_, verb) = Describe(e.Kind);
        var lines = new List<string> { $"{e.MonitorName} {verb}.", $"Time (UTC): {e.At:u}" };
        if (FormatResponseTime(e) is { } timing) lines.Add($"Response time: {timing}");
        if (!string.IsNullOrWhiteSpace(e.Message)) lines.Add($"Detail: {e.Message}");

        foreach (var line in IncidentLines(e)) lines.Add(line);
        foreach (var line in EnrichmentLines(e)) lines.Add(line);

        return string.Join('\n', lines);
    }

    public static string Html(NotificationEvent e)
    {
        var (_, verb) = Describe(e.Kind);
        var name = WebUtility.HtmlEncode(e.MonitorName);
        var timing = FormatResponseTime(e) is { } t ? $"<p>Response time: {WebUtility.HtmlEncode(t)}</p>" : "";
        var detail = string.IsNullOrWhiteSpace(e.Message) ? "" : $"<p>Detail: {WebUtility.HtmlEncode(e.Message)}</p>";

        var extra = string.Concat(IncidentLines(e).Concat(EnrichmentLines(e))
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

    /// <summary>The "what broke" context. Each line is omitted when the underlying value is unknown.</summary>
    private static IEnumerable<string> EnrichmentLines(NotificationEvent e)
    {
        if (e.Enrichment is not { } x) yield break;

        if (!string.IsNullOrWhiteSpace(x.LastStatusCode)) yield return $"Last response code: {x.LastStatusCode}";
        if (!string.IsNullOrWhiteSpace(x.ResolvedAddress)) yield return $"Resolved to: {x.ResolvedAddress}";

        if (x.RecentResponseTimesMs.Count > 0)
        {
            var series = string.Join(", ", x.RecentResponseTimesMs
                .Select(ms => string.Create(CultureInfo.InvariantCulture, $"{ms:N0}")));
            yield return $"Recent response times (ms, oldest first): {series}";
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
    /// Response time for the alert body, or null when the probe recorded none. Carries the most weight on
    /// a Degraded alert — "responding slowly" is not actionable without the number behind it — but it is
    /// useful context on any transition.
    /// </summary>
    private static string? FormatResponseTime(NotificationEvent e)
        => e.ResponseTimeMs is { } ms
            ? string.Create(CultureInfo.InvariantCulture, $"{ms:N0} ms")
            : null;
}

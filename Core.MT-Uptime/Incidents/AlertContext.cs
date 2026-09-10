namespace MT.Uptime.Core.Incidents;

/// <summary>
/// What the alert needs to say about the incident it belongs to.
/// <para>
/// Correlation is worth little if it only shows up in the dashboard: the person being woken at 03:00 is
/// reading a notification, not a web page. This is what turns "acme-web is DOWN" — the twentieth such
/// message in a minute — into "acme-web is DOWN, along with 19 others on 203.0.113.10".
/// </para>
/// </summary>
public sealed record IncidentSummary(
    long Id,
    int MonitorCount,
    string? SharedInfrastructure,
    IReadOnlyList<string> OtherAffectedMonitors,
    DateTime StartedAt,
    bool Acknowledged)
{
    /// <summary>True when this alert is one of several monitors failing together.</summary>
    public bool IsCorrelated => MonitorCount > 1;
}

/// <summary>
/// Context gathered at dispatch time so the alert answers <i>what broke</i>, not just <i>that</i>
/// something did. Every field is optional: enrichment must never be the reason an alert fails to send.
/// </summary>
/// <param name="ResolvedAddress">The address the monitor's host resolved to, via the correlation key.</param>
/// <param name="LastGoodStatusCode">
/// The protocol code from the last check that <em>succeeded</em>, with its timing and age alongside.
/// <para>
/// This was <c>LastStatusCode</c>, read as "the newest heartbeat's code", and it was a race. The
/// heartbeat writer flushes asynchronously, so at dispatch the failing beat may or may not be in the
/// table: whoever won decided whether the field showed the failure (duplicating the alert's own
/// <c>Detail</c> line) or the state before it. The alert that prompted this work printed
/// <c>Detail: Unexpected status 521</c> directly above <c>Last response code: 200</c>, which reads as
/// a contradiction and is really just the two halves of that race showing at once.
/// </para>
/// <para>
/// Resolved by making the field mean the useful half explicitly — the last <em>good</em> response —
/// and filtering on status rather than on recency, so the answer no longer depends on flush timing.
/// The current code travels on <see cref="Notifications.NotificationEvent.StatusCode"/> instead.
/// </para>
/// </param>
/// <param name="RecentResponseTimesMs">
/// The response times immediately before the alert, oldest first. Deliberately excludes the failing
/// check — the point of the series is the baseline it broke from, and including a 21,556 ms outlier at
/// the end squashes the healthy numbers into noise.
/// </param>
/// <param name="PreviousStateSince">
/// When the monitor entered the state it held before this alert, so the alert can say "up for 4d 3h
/// before this". A monitor that has been solid for a week failing is a different event from one that
/// has been flapping all evening, and the alert currently cannot tell them apart.
/// </param>
public sealed record AlertEnrichment(
    string? ResolvedAddress,
    string? LastGoodStatusCode,
    double? LastGoodResponseTimeMs,
    DateTime? LastGoodAt,
    IReadOnlyList<double> RecentResponseTimesMs,
    DateTime? CertificateExpiresAt,
    DateTime? PreviousStateSince = null)
{
    /// <summary>
    /// Certificates are only mentioned when they are near expiry or already expired — see
    /// <see cref="ExpiryThreshold"/>. A certificate good for another nine months is not a clue, and
    /// printing it on every alert trains people to skip the detail lines.
    /// </summary>
    public static readonly TimeSpan ExpiryThreshold = TimeSpan.FromDays(30);

    public static bool IsWorthMentioning(DateTime? expiresAt, DateTime now)
        => expiresAt is { } e && e - now <= ExpiryThreshold;
}

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
public sealed record AlertEnrichment(
    string? ResolvedAddress,
    string? LastStatusCode,
    IReadOnlyList<double> RecentResponseTimesMs,
    DateTime? CertificateExpiresAt)
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

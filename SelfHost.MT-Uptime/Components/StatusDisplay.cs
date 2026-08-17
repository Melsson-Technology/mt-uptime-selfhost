namespace MT.Uptime.Web.Components;

/// <summary>
/// One place that decides how a <see cref="MonitorStatus"/> looks. Dashboard, monitor detail, the public
/// status page and the heartbeat bar all render the same status, and each used to carry its own copy of
/// this mapping with a catch-all fallback — which quietly mislabels any status added later.
/// </summary>
public static class StatusDisplay
{
    /// <summary>CSS modifier for the small round status dot (see <c>.dot</c> in app.css).</summary>
    public static string DotClass(MonitorStatus s) => s switch
    {
        MonitorStatus.Up => "up",
        MonitorStatus.Down => "down",
        MonitorStatus.Degraded => "degraded",
        _ => "pending",
    };

    /// <summary>CSS modifier for coloured status text (see <c>.stat-value</c> / <c>.s-status</c>).</summary>
    public static string BadgeClass(MonitorStatus s) => s switch
    {
        MonitorStatus.Up => "ok",
        MonitorStatus.Down => "bad",
        MonitorStatus.Degraded => "degraded",
        _ => "warn",
    };

    /// <summary>Admin-facing label.</summary>
    public static string Text(MonitorStatus s) => s switch
    {
        MonitorStatus.Up => "Up",
        MonitorStatus.Down => "Down",
        MonitorStatus.Degraded => "Slow",
        _ => "Pending",
    };

    /// <summary>
    /// Public status-page label. Deliberately softer than the admin wording: visitors care whether the
    /// service works, and a degraded service does work.
    /// </summary>
    public static string PublicText(MonitorStatus s) => s switch
    {
        MonitorStatus.Up => "Operational",
        MonitorStatus.Down => "Down",
        MonitorStatus.Degraded => "Degraded performance",
        _ => "Checking",
    };

    /// <summary>
    /// Literal fill for the inline-SVG heartbeat bar. Hard-coded rather than a CSS variable because
    /// these are SVG <c>fill</c> attributes; keep in step with the palette at the top of app.css.
    /// </summary>
    public static string BarColor(MonitorStatus s) => s switch
    {
        MonitorStatus.Up => "#22a06b",
        MonitorStatus.Down => "#e5484d",
        MonitorStatus.Degraded => "#e8830c",
        _ => "#f5a623",
    };
}

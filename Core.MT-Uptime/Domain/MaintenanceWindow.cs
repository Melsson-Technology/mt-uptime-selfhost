namespace MT.Uptime.Core.Domain;

/// <summary>
/// A period during which failures are expected and must not page anyone.
/// <para>
/// A window changes two things and deliberately not a third: it <b>suppresses alerts</b>, and it
/// <b>excludes the affected beats from uptime</b>. It does not change what was recorded — a monitor that
/// was down during a window still has Down heartbeats, still opens an event, and still appears in the
/// history. Rewriting the record to look healthy would make the maintenance feature a way of falsifying
/// the very number the product is trusted for.
/// </para>
/// </summary>
public class MaintenanceWindow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;

    public MaintenanceRecurrence Recurrence { get; set; }

    // --- One-off (Recurrence == Once): absolute instants, stored UTC ---

    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }

    // --- Recurring (Recurrence == Weekly) ---

    /// <summary>Minutes past local midnight at which the window opens (e.g. 120 = 02:00).</summary>
    public int StartMinuteOfDay { get; set; }

    /// <summary>How long the window stays open. May run past midnight into the following day.</summary>
    public int DurationMinutes { get; set; } = 60;

    /// <summary>
    /// Bitmask of the days the window opens, bit 0 = Sunday through bit 6 = Saturday. All seven bits set
    /// is a daily window, which is why there is no separate "daily" recurrence to keep in step.
    /// </summary>
    public int DaysOfWeekMask { get; set; }

    /// <summary>
    /// The zone <see cref="StartMinuteOfDay"/> is expressed in. Recurring maintenance is scheduled by
    /// wall-clock ("Sundays at 02:00"), so storing it in UTC would silently move the window by an hour
    /// twice a year. Falls back to UTC if the id is not known to the host.
    /// </summary>
    public string TimeZoneId { get; set; } = "UTC";

    // --- Scope ---

    /// <summary>Covers every monitor, present and future. Set for whole-instance maintenance.</summary>
    public bool AppliesToAllMonitors { get; set; }

    public ICollection<MaintenanceWindowMonitor> Monitors { get; set; } = new List<MaintenanceWindowMonitor>();
    public ICollection<MaintenanceWindowTag> Tags { get; set; } = new List<MaintenanceWindowTag>();

    /// <summary>Announce this window on any status page carrying an affected monitor.</summary>
    public bool Publish { get; set; } = true;
}

/// <summary>Explicit monitor scope for a <see cref="MaintenanceWindow"/>.</summary>
public class MaintenanceWindowMonitor
{
    public int MaintenanceWindowId { get; set; }
    public MaintenanceWindow? MaintenanceWindow { get; set; }

    public int MonitorId { get; set; }
    public Monitor? Monitor { get; set; }
}

/// <summary>
/// Tag scope for a <see cref="MaintenanceWindow"/>: every monitor carrying the tag is covered, including
/// ones tagged after the window was created. This is what tags were built as the substrate for.
/// </summary>
public class MaintenanceWindowTag
{
    public int MaintenanceWindowId { get; set; }
    public MaintenanceWindow? MaintenanceWindow { get; set; }

    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}

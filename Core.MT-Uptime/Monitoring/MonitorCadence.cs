namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Everything governing <em>how hard the engine may poll a target</em>, in one place so the edit form and
/// the runner cannot disagree.
/// <para>
/// The invariant that matters: a single check must never outlive its own interval. A check that overruns
/// leaves <see cref="PeriodicTimer"/> with a tick already pending, so the next probe starts with no gap —
/// continuous polling of a target that is, by definition, already slow. Everything else here is about not
/// generating pointless traffic against infrastructure that is often somebody else's.
/// </para>
/// All members are pure, so the guardrails are unit-tested directly rather than through the UI.
/// </summary>
public static class MonitorCadence
{
    /// <summary>Shortest polling interval the engine will honour, whatever a row asks for.</summary>
    public const int MinIntervalSeconds = 5;

    /// <summary>Longest a single check may run, independent of the interval.</summary>
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Below this the UI warns (without blocking) that the interval is mostly adding load.</summary>
    public const int LowIntervalWarnSeconds = 30;

    /// <summary>Effective polling interval for a monitor row.</summary>
    public static TimeSpan ResolveInterval(int configuredSeconds)
        => TimeSpan.FromSeconds(Math.Max(MinIntervalSeconds, configuredSeconds));

    /// <summary>
    /// Effective per-check timeout: clamped to [1s, <see cref="MaxTimeoutSeconds"/>] and always at least a
    /// second shorter than the interval. <see cref="ValidateCadence"/> rejects the bad combination up front;
    /// this clamp is the backstop for rows created any other way (import, direct DB edit, or saved before
    /// the rule existed).
    /// </summary>
    public static TimeSpan ResolveTimeout(int configuredSeconds, TimeSpan interval)
    {
        var ceiling = Math.Min(MaxTimeoutSeconds, Math.Max(1, (int)interval.TotalSeconds - 1));
        return TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 1, ceiling));
    }

    /// <summary>
    /// Blocking validation for an actively-probed monitor. Returns null when the cadence is acceptable,
    /// otherwise the reason to show the user. (Push monitors are passive and have no timeout — skip them.)
    /// </summary>
    public static string? ValidateCadence(int intervalSeconds, int timeoutSeconds)
    {
        if (intervalSeconds < MinIntervalSeconds)
            return $"Interval must be at least {MinIntervalSeconds} seconds.";

        if (timeoutSeconds >= intervalSeconds)
            return $"Timeout ({timeoutSeconds}s) must be shorter than the interval ({intervalSeconds}s). "
                 + "Otherwise a slow target is re-checked the instant the previous check gives up, with no "
                 + "gap — piling on load exactly when it is struggling.";

        return null;
    }

    /// <summary>
    /// Whether a successful probe counts as slow. Decided centrally rather than in each checker, so one
    /// threshold covers every monitor type that measures a response time.
    /// <para>
    /// A null or non-positive threshold means the feature is off. A null response time (some probes record
    /// none) can never be slow — absence of a measurement is not evidence of slowness.
    /// </para>
    /// </summary>
    public static bool IsSlow(double? responseMs, int? thresholdMs)
        => thresholdMs is > 0 && responseMs is { } ms && ms > thresholdMs.Value;

    /// <summary>
    /// Blocking validation for the slow-response threshold. Returns null when it is off or usable.
    /// <para>
    /// A threshold at or above the timeout can never fire: the check is abandoned as a failure before it
    /// ever gets slow enough to count, so the monitor would silently go Down instead of Slow and the
    /// setting would look broken rather than misconfigured.
    /// </para>
    /// </summary>
    public static string? ValidateSlowThreshold(int? thresholdMs, int timeoutSeconds)
    {
        if (thresholdMs is not > 0) return null; // blank/0 = feature off

        var timeoutMs = timeoutSeconds * 1000;
        if (thresholdMs >= timeoutMs)
            return $"Slow threshold ({thresholdMs}ms) must be below the timeout ({timeoutSeconds}s = {timeoutMs}ms). "
                 + "A check that passes the timeout is abandoned as a failure, so a threshold at or above it "
                 + "could never be reached — the monitor would go Down instead of Slow.";

        return null;
    }

    /// <summary>
    /// Non-blocking nudge about a very short interval. Short intervals rarely detect an outage meaningfully
    /// sooner (the retry window dominates) but do multiply steady load on the target, so this warns rather
    /// than blocks. Returns null when there is nothing worth saying.
    /// </summary>
    public static string? LowIntervalWarning(int intervalSeconds)
    {
        // Below the floor ValidateCadence blocks outright, so there is no point warning as well.
        if (intervalSeconds is < MinIntervalSeconds or >= LowIntervalWarnSeconds) return null;

        var perMinute = 60d / intervalSeconds;
        return $"A {intervalSeconds}s interval means about {perMinute:0.#} checks a minute against this "
             + "target, continuously. 60s is plenty for uptime monitoring — shorter intervals mostly add "
             + "load rather than detecting outages sooner. You can still save this.";
    }

    /// <summary>
    /// Per-type polling cadence. TLS and DNS answers change on the order of days, so probing them at the
    /// generic 60s default is thousands of pointless handshakes/queries a day — and a full TLS handshake in
    /// particular is far from free for the far end.
    /// </summary>
    public static int DefaultIntervalFor(MonitorType type) => type switch
    {
        MonitorType.Tls => 21_600,  // 6h — certificate expiry moves once a day at most
        MonitorType.Dns => 3_600,   // 1h — records change on the order of hours/days
        MonitorType.Push => 3_600,  // hourly suits cron jobs
        _ => 60,
    };

    /// <summary>
    /// True when an interval is still one of the values <see cref="DefaultIntervalFor"/> hands out, i.e. the
    /// user has not typed their own. Lets a type change re-pick the cadence without discarding a deliberate
    /// choice.
    /// </summary>
    public static bool IsUntouchedDefault(int intervalSeconds)
        => intervalSeconds is 60 or 3_600 or 21_600;
}

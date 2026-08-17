namespace MT.Uptime.Core.Monitoring;

public enum NotifyKind { None, Down, Up, ResendDown, Degraded }

public enum EventAction
{
    None,
    Open,
    Resolve,

    /// <summary>
    /// Close the open incident and start a new one in the same beat. Used when one incident escalates or
    /// de-escalates directly into another (Degraded → Down, Down → Degraded) so the event log never has
    /// two open incidents for a monitor, which would confuse the "resolve the latest open one" lookup.
    /// </summary>
    ResolveAndOpen,
}

/// <summary>The engine's decision for a single check: what to record, whether it's a transition, and what to notify.</summary>
public sealed record StateDecision(
    MonitorStatus HeartbeatStatus,
    bool Important,
    int Attempt,
    MonitorStatus PreviousConfirmed,
    MonitorStatus NewConfirmed,
    NotifyKind Notify,
    EventAction EventAction);

/// <summary>
/// Per-monitor state machine with a retry threshold for outages, an independent sustained-slow threshold
/// for degradation, and optional resend-while-down.
/// <para>
/// Two debounce windows run side by side and never interfere: consecutive failures build toward Down
/// (<c>retryCount</c>), consecutive slow-but-successful checks build toward Degraded
/// (<c>degradedAfterChecks</c>). Response time is spiky, so a single slow sample is noise — Degraded is
/// only confirmed once the target has been slow N checks running. Degraded still means *available*: the
/// target answered correctly, so it counts as up for uptime %.
/// </para>
/// Not thread-safe by design: each <see cref="MonitorRunner"/> owns one instance and drives it from its
/// single sequential check loop.
/// </summary>
public sealed class MonitorStateMachine
{
    private readonly int _retryCount;
    private readonly bool _upsideDown;
    private readonly int _resendEveryN;
    private readonly int _degradedAfter;
    private int _failCount;
    private int _slowCount;
    private int _downNotifyCounter;

    public MonitorStatus ConfirmedStatus { get; private set; }

    /// <summary>Consecutive slow checks seen so far, for the "degraded (2/3)" style progress in logs/UI.</summary>
    public int SlowStreak => _slowCount;

    public MonitorStateMachine(
        int retryCount,
        bool upsideDown,
        int resendEveryN,
        MonitorStatus initial,
        int degradedAfterChecks = 3)
    {
        _retryCount = Math.Max(0, retryCount);
        _upsideDown = upsideDown;
        _resendEveryN = Math.Max(0, resendEveryN);
        // At least one slow check must be observed, whatever the row says (a stored 0 would otherwise
        // mean "confirm on a sample we haven't taken").
        _degradedAfter = Math.Max(1, degradedAfterChecks);
        ConfirmedStatus = initial;
    }

    /// <param name="hard">
    /// True when the failure is a definitive negative answer (e.g. a bad HTTP status). A hard failure
    /// confirms Down immediately, bypassing the retry window. Ignored unless the effective result is Down.
    /// </param>
    /// <param name="slow">
    /// True when the check succeeded but exceeded the monitor's response-time threshold. Ignored unless
    /// the effective result is Up — a failed check is already worse news than a slow one.
    /// </param>
    public StateDecision Evaluate(CheckStatus raw, bool hard = false, bool slow = false)
    {
        var effective = _upsideDown
            ? (raw == CheckStatus.Up ? CheckStatus.Down : CheckStatus.Up)
            : raw;
        var prev = ConfirmedStatus;

        if (effective == CheckStatus.Up)
        {
            _failCount = 0;
            _downNotifyCounter = 0;
            return slow ? EvaluateSlow(prev) : EvaluateHealthy(prev);
        }

        // effective == Down. An outage supersedes any slow streak that was building.
        _slowCount = 0;
        _failCount++;

        if (prev == MonitorStatus.Down)
        {
            _downNotifyCounter++;
            var resend = _resendEveryN > 0 && _downNotifyCounter % _resendEveryN == 0;
            return new StateDecision(MonitorStatus.Down, false, _failCount, prev, ConfirmedStatus,
                resend ? NotifyKind.ResendDown : NotifyKind.None, EventAction.None);
        }

        // A hard failure (definitive bad answer) skips the retry window; a soft failure must exhaust it.
        if (hard || _failCount > _retryCount)
        {
            ConfirmedStatus = MonitorStatus.Down;
            _downNotifyCounter = 0;
            // Escalating out of a confirmed Degraded incident: close it and open the outage in one beat.
            var action = prev == MonitorStatus.Degraded ? EventAction.ResolveAndOpen : EventAction.Open;
            return new StateDecision(MonitorStatus.Down, true, _failCount, prev, ConfirmedStatus,
                NotifyKind.Down, action);
        }

        // still inside the retry window — record a pending beat, no alert yet
        return new StateDecision(MonitorStatus.Pending, false, _failCount, prev, ConfirmedStatus, NotifyKind.None, EventAction.None);
    }

    /// <summary>A successful check within the response-time threshold: the fully-healthy path.</summary>
    private StateDecision EvaluateHealthy(MonitorStatus prev)
    {
        _slowCount = 0;

        if (prev == MonitorStatus.Up)
            return new StateDecision(MonitorStatus.Up, false, 0, prev, ConfirmedStatus, NotifyKind.None, EventAction.None);

        ConfirmedStatus = MonitorStatus.Up;

        // Only a real recovery (was confirmed Down or Degraded) is an incident/notification; a first-ever
        // or post-pending success just goes green silently.
        var recovered = prev is MonitorStatus.Down or MonitorStatus.Degraded;
        return new StateDecision(MonitorStatus.Up, recovered, 0, prev, ConfirmedStatus,
            recovered ? NotifyKind.Up : NotifyKind.None,
            recovered ? EventAction.Resolve : EventAction.None);
    }

    /// <summary>A successful but slow check: build toward Degraded, confirming only once sustained.</summary>
    private StateDecision EvaluateSlow(MonitorStatus prev)
    {
        _slowCount++;

        // Already degraded: keep recording degraded beats, but don't re-alert.
        if (prev == MonitorStatus.Degraded)
            return new StateDecision(MonitorStatus.Degraded, false, _slowCount, prev, ConfirmedStatus,
                NotifyKind.None, EventAction.None);

        if (_slowCount >= _degradedAfter)
        {
            ConfirmedStatus = MonitorStatus.Degraded;
            // Recovering from an outage straight into a slow state: close the outage, open the degradation.
            var action = prev == MonitorStatus.Down ? EventAction.ResolveAndOpen : EventAction.Open;
            return new StateDecision(MonitorStatus.Degraded, true, _slowCount, prev, ConfirmedStatus,
                NotifyKind.Degraded, action);
        }

        // Still building the streak. The beat is recorded as Degraded so the history and heartbeat bar
        // show the slowdown, but the confirmed status (and therefore alerting) has not moved yet —
        // the same shape as Pending during the retry window.
        //
        // One exception: while still confirmed Down, a slow success is not yet enough to say we have
        // recovered, so keep reporting Down until either the streak confirms Degraded or a fast check
        // clears it outright.
        if (prev == MonitorStatus.Down)
            return new StateDecision(MonitorStatus.Down, false, _slowCount, prev, ConfirmedStatus,
                NotifyKind.None, EventAction.None);

        return new StateDecision(MonitorStatus.Degraded, false, _slowCount, prev, ConfirmedStatus,
            NotifyKind.None, EventAction.None);
    }
}

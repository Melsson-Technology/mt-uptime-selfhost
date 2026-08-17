using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// The sustained-degraded path: a check that succeeds but exceeds the monitor's response-time threshold.
/// Degraded is only confirmed (and alerted) after N consecutive slow checks, because response time is
/// spiky and a single slow sample is noise. Degraded still means available — the target answered.
/// </summary>
public class DegradedStateTests
{
    private static MonitorStateMachine New(int retries = 1, int degradedAfter = 3,
        MonitorStatus initial = MonitorStatus.Up, bool upsideDown = false, int resend = 0)
        => new(retries, upsideDown, resend, initial, degradedAfter);

    // --- Building the streak --------------------------------------------------------------------

    [Fact]
    public void A_single_slow_check_records_degraded_but_does_not_confirm_or_alert()
    {
        var sm = New(degradedAfter: 3);
        var d = sm.Evaluate(CheckStatus.Up, slow: true);

        // The beat shows the slowdown immediately...
        Assert.Equal(MonitorStatus.Degraded, d.HeartbeatStatus);
        // ...but nothing is confirmed, alerted, or logged as an incident yet.
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);
        Assert.False(d.Important);
        Assert.Equal(NotifyKind.None, d.Notify);
        Assert.Equal(EventAction.None, d.EventAction);
    }

    [Fact]
    public void Degraded_is_confirmed_and_alerted_only_on_the_nth_consecutive_slow_check()
    {
        var sm = New(degradedAfter: 3);

        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Up, slow: true).Notify);   // 1/3
        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Up, slow: true).Notify);   // 2/3
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);

        var confirmed = sm.Evaluate(CheckStatus.Up, slow: true);                          // 3/3
        Assert.Equal(MonitorStatus.Degraded, confirmed.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Degraded, sm.ConfirmedStatus);
        Assert.True(confirmed.Important);
        Assert.Equal(NotifyKind.Degraded, confirmed.Notify);
        Assert.Equal(EventAction.Open, confirmed.EventAction);
    }

    [Fact]
    public void The_slow_streak_is_reported_as_the_attempt_number()
    {
        var sm = New(degradedAfter: 3);

        Assert.Equal(1, sm.Evaluate(CheckStatus.Up, slow: true).Attempt);
        Assert.Equal(2, sm.Evaluate(CheckStatus.Up, slow: true).Attempt);
        Assert.Equal(3, sm.Evaluate(CheckStatus.Up, slow: true).Attempt);
        Assert.Equal(3, sm.SlowStreak);
    }

    [Fact]
    public void One_fast_check_resets_the_streak()
    {
        var sm = New(degradedAfter: 3);
        sm.Evaluate(CheckStatus.Up, slow: true);  // 1/3
        sm.Evaluate(CheckStatus.Up, slow: true);  // 2/3

        var fast = sm.Evaluate(CheckStatus.Up);   // recovered before confirming — silent
        Assert.Equal(MonitorStatus.Up, fast.HeartbeatStatus);
        Assert.False(fast.Important);
        Assert.Equal(NotifyKind.None, fast.Notify);
        Assert.Equal(EventAction.None, fast.EventAction);
        Assert.Equal(0, sm.SlowStreak);

        // The next slow run must start from scratch rather than confirming on one more sample.
        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Up, slow: true).Notify);
        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Up, slow: true).Notify);
        Assert.Equal(NotifyKind.Degraded, sm.Evaluate(CheckStatus.Up, slow: true).Notify);
    }

    [Fact]
    public void Degraded_after_one_confirms_on_the_first_slow_check()
    {
        var sm = New(degradedAfter: 1);
        var d = sm.Evaluate(CheckStatus.Up, slow: true);

        Assert.Equal(MonitorStatus.Degraded, sm.ConfirmedStatus);
        Assert.Equal(NotifyKind.Degraded, d.Notify);
    }

    [Fact]
    public void A_stored_zero_threshold_is_treated_as_one_not_as_confirm_on_no_evidence()
    {
        var sm = New(degradedAfter: 0);
        var d = sm.Evaluate(CheckStatus.Up, slow: true);

        Assert.Equal(MonitorStatus.Degraded, sm.ConfirmedStatus);
        Assert.Equal(1, d.Attempt);
    }

    // --- While degraded -------------------------------------------------------------------------

    [Fact]
    public void Staying_degraded_does_not_re_alert()
    {
        var sm = New(degradedAfter: 1);
        Assert.Equal(NotifyKind.Degraded, sm.Evaluate(CheckStatus.Up, slow: true).Notify);

        for (var i = 0; i < 5; i++)
        {
            var beat = sm.Evaluate(CheckStatus.Up, slow: true);
            Assert.Equal(MonitorStatus.Degraded, beat.HeartbeatStatus);
            Assert.False(beat.Important);
            Assert.Equal(NotifyKind.None, beat.Notify);
            Assert.Equal(EventAction.None, beat.EventAction);
        }
    }

    [Fact]
    public void Recovering_from_degraded_to_fast_alerts_and_resolves()
    {
        var sm = New(degradedAfter: 1);
        sm.Evaluate(CheckStatus.Up, slow: true); // -> Degraded

        var up = sm.Evaluate(CheckStatus.Up);
        Assert.Equal(MonitorStatus.Up, up.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);
        Assert.True(up.Important);
        Assert.Equal(NotifyKind.Up, up.Notify);
        Assert.Equal(EventAction.Resolve, up.EventAction);
    }

    // --- Interaction with outages ---------------------------------------------------------------

    [Fact]
    public void Degraded_escalating_to_down_closes_the_degradation_and_opens_the_outage()
    {
        var sm = New(retries: 0, degradedAfter: 1);
        sm.Evaluate(CheckStatus.Up, slow: true); // -> Degraded

        var down = sm.Evaluate(CheckStatus.Down);
        Assert.Equal(MonitorStatus.Down, sm.ConfirmedStatus);
        Assert.Equal(NotifyKind.Down, down.Notify);
        Assert.Equal(MonitorStatus.Degraded, down.PreviousConfirmed);
        // Both in one beat, so the event log never holds two open incidents for this monitor.
        Assert.Equal(EventAction.ResolveAndOpen, down.EventAction);
    }

    [Fact]
    public void An_outage_wipes_a_building_slow_streak()
    {
        var sm = New(retries: 0, degradedAfter: 3);
        sm.Evaluate(CheckStatus.Up, slow: true); // 1/3
        sm.Evaluate(CheckStatus.Up, slow: true); // 2/3

        sm.Evaluate(CheckStatus.Down);           // -> Down, streak discarded
        Assert.Equal(0, sm.SlowStreak);

        // Coming back slow must rebuild the full streak, not resume at 3/3.
        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Up, slow: true).Notify);
        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Up, slow: true).Notify);
        Assert.Equal(NotifyKind.Degraded, sm.Evaluate(CheckStatus.Up, slow: true).Notify);
    }

    [Fact]
    public void While_down_a_slow_success_is_not_yet_a_recovery()
    {
        var sm = New(retries: 0, degradedAfter: 3);
        sm.Evaluate(CheckStatus.Down); // -> Down

        // One slow success is not enough to claim recovery; the monitor keeps reporting Down.
        var beat = sm.Evaluate(CheckStatus.Up, slow: true);
        Assert.Equal(MonitorStatus.Down, beat.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Down, sm.ConfirmedStatus);
        Assert.Equal(NotifyKind.None, beat.Notify);
        Assert.Equal(EventAction.None, beat.EventAction);
    }

    [Fact]
    public void Down_recovering_into_a_sustained_slow_state_closes_the_outage_and_opens_a_degradation()
    {
        var sm = New(retries: 0, degradedAfter: 2);
        sm.Evaluate(CheckStatus.Down);                    // -> Down
        sm.Evaluate(CheckStatus.Up, slow: true);          // 1/2, still Down

        var degraded = sm.Evaluate(CheckStatus.Up, slow: true); // 2/2
        Assert.Equal(MonitorStatus.Degraded, sm.ConfirmedStatus);
        Assert.Equal(MonitorStatus.Down, degraded.PreviousConfirmed);
        Assert.Equal(NotifyKind.Degraded, degraded.Notify);
        Assert.Equal(EventAction.ResolveAndOpen, degraded.EventAction);
    }

    [Fact]
    public void A_fast_check_recovers_straight_from_down_even_after_slow_beats()
    {
        var sm = New(retries: 0, degradedAfter: 3);
        sm.Evaluate(CheckStatus.Down);           // -> Down
        sm.Evaluate(CheckStatus.Up, slow: true); // 1/3, still Down

        var up = sm.Evaluate(CheckStatus.Up);    // fast — unambiguous recovery
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);
        Assert.Equal(NotifyKind.Up, up.Notify);
        Assert.Equal(EventAction.Resolve, up.EventAction);
    }

    [Fact]
    public void Slow_does_not_interfere_with_the_retry_window()
    {
        // A slow flag on a *failed* check is irrelevant — the failure is already worse news.
        var sm = New(retries: 1, degradedAfter: 1);

        var pending = sm.Evaluate(CheckStatus.Down, slow: true);
        Assert.Equal(MonitorStatus.Pending, pending.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);

        var down = sm.Evaluate(CheckStatus.Down, slow: true);
        Assert.Equal(MonitorStatus.Down, down.HeartbeatStatus);
        Assert.Equal(NotifyKind.Down, down.Notify);
    }

    [Fact]
    public void Upside_down_ignores_slow_when_the_result_inverts_to_down()
    {
        // Inverted monitor: raw Up is the *bad* outcome, so it goes down the failure path and the
        // slow flag must not produce a degraded beat.
        var sm = New(retries: 0, degradedAfter: 1, upsideDown: true);

        var d = sm.Evaluate(CheckStatus.Up, slow: true);
        Assert.Equal(MonitorStatus.Down, d.HeartbeatStatus);
        Assert.Equal(NotifyKind.Down, d.Notify);
    }

    [Fact]
    public void Degradation_never_fires_when_no_check_is_ever_slow()
    {
        // The default posture: with no threshold configured the runner never passes slow, so a monitor
        // must behave exactly as it did before this feature existed.
        var sm = New(retries: 1, degradedAfter: 3);

        for (var i = 0; i < 10; i++)
        {
            var beat = sm.Evaluate(CheckStatus.Up);
            Assert.Equal(MonitorStatus.Up, beat.HeartbeatStatus);
            Assert.Equal(NotifyKind.None, beat.Notify);
        }
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);
    }
}

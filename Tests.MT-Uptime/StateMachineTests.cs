using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

public class StateMachineTests
{
    private static MonitorStateMachine New(int retries = 1, bool upsideDown = false, int resend = 0,
        MonitorStatus initial = MonitorStatus.Up)
        => new(retries, upsideDown, resend, initial);

    [Fact]
    public void Hard_failure_confirms_down_immediately_skipping_pending()
    {
        var sm = New(retries: 1);
        var d = sm.Evaluate(CheckStatus.Down, hard: true);

        Assert.Equal(MonitorStatus.Down, d.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Down, sm.ConfirmedStatus);
        Assert.True(d.Important);
        Assert.Equal(EventAction.Open, d.EventAction);
        Assert.Equal(NotifyKind.Down, d.Notify);
    }

    [Fact]
    public void Soft_failure_pends_first_then_downs_after_retry_threshold()
    {
        var sm = New(retries: 1);

        var first = sm.Evaluate(CheckStatus.Down); // soft
        Assert.Equal(MonitorStatus.Pending, first.HeartbeatStatus);
        Assert.Equal(NotifyKind.None, first.Notify);
        Assert.Equal(EventAction.None, first.EventAction);

        var second = sm.Evaluate(CheckStatus.Down); // soft
        Assert.Equal(MonitorStatus.Down, second.HeartbeatStatus);
        Assert.Equal(NotifyKind.Down, second.Notify);
        Assert.Equal(EventAction.Open, second.EventAction);
    }

    [Fact]
    public void Hard_failure_beats_a_large_retry_count()
    {
        var sm = New(retries: 5); // would normally need 6 soft failures
        var d = sm.Evaluate(CheckStatus.Down, hard: true);
        Assert.Equal(MonitorStatus.Down, sm.ConfirmedStatus);
        Assert.True(d.Important);
    }

    [Fact]
    public void Hard_down_then_success_recovers_and_resolves()
    {
        var sm = New(retries: 1);
        sm.Evaluate(CheckStatus.Down, hard: true); // -> Down

        var up = sm.Evaluate(CheckStatus.Up);
        Assert.Equal(MonitorStatus.Up, up.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);
        Assert.True(up.Important);
        Assert.Equal(EventAction.Resolve, up.EventAction);
        Assert.Equal(NotifyKind.Up, up.Notify);
    }

    [Fact]
    public void Upside_down_treats_a_bad_status_as_up_ignoring_hard()
    {
        // With UpsideDown, a bad status (raw Down, hard) is the desired outcome -> effective Up.
        var sm = New(retries: 1, upsideDown: true);
        var d = sm.Evaluate(CheckStatus.Down, hard: true);

        Assert.Equal(MonitorStatus.Up, d.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);
    }

    [Fact]
    public void Upside_down_downs_when_the_target_is_reachable()
    {
        // Inverted monitor: a raw Up (target alive) is the *bad* outcome -> effective Down.
        var sm = New(retries: 1, upsideDown: true);

        var first = sm.Evaluate(CheckStatus.Up); // effective Down, soft
        Assert.Equal(MonitorStatus.Pending, first.HeartbeatStatus);

        var second = sm.Evaluate(CheckStatus.Up); // exhausts the retry window
        Assert.Equal(MonitorStatus.Down, second.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Down, sm.ConfirmedStatus);
        Assert.Equal(NotifyKind.Down, second.Notify);
        Assert.Equal(EventAction.Open, second.EventAction);
    }

    [Fact]
    public void A_soft_failure_that_recovers_before_confirming_down_goes_up_silently()
    {
        // Pending -> Up must NOT fire a recovery alert or resolve an event: it never confirmed Down.
        var sm = New(retries: 1);
        var pending = sm.Evaluate(CheckStatus.Down); // soft -> Pending, no event opened
        Assert.Equal(MonitorStatus.Pending, pending.HeartbeatStatus);

        var up = sm.Evaluate(CheckStatus.Up);
        Assert.Equal(MonitorStatus.Up, up.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus);
        Assert.False(up.Important);
        Assert.Equal(NotifyKind.None, up.Notify);
        Assert.Equal(EventAction.None, up.EventAction);
    }

    [Fact]
    public void The_first_ever_check_being_up_is_silent()
    {
        var sm = New(retries: 1, initial: MonitorStatus.Up);
        var up = sm.Evaluate(CheckStatus.Up);

        Assert.Equal(MonitorStatus.Up, up.HeartbeatStatus);
        Assert.False(up.Important);
        Assert.Equal(NotifyKind.None, up.Notify);
        Assert.Equal(EventAction.None, up.EventAction);
    }

    [Fact]
    public void Recovery_resets_the_retry_window()
    {
        // After a confirmed down -> up, a single fresh soft failure must pend again, not re-down instantly.
        var sm = New(retries: 1);
        sm.Evaluate(CheckStatus.Down, hard: true); // -> Down
        sm.Evaluate(CheckStatus.Up);               // -> Up (window should reset)

        var next = sm.Evaluate(CheckStatus.Down);  // soft
        Assert.Equal(MonitorStatus.Pending, next.HeartbeatStatus);
        Assert.Equal(MonitorStatus.Up, sm.ConfirmedStatus); // still up until the window is exhausted
    }

    [Fact]
    public void Staying_down_is_silent_when_resend_is_off()
    {
        var sm = New(retries: 0, resend: 0);
        var down = sm.Evaluate(CheckStatus.Down); // retries:0 -> confirmed Down at once
        Assert.Equal(NotifyKind.Down, down.Notify);

        for (var i = 0; i < 5; i++)
        {
            var beat = sm.Evaluate(CheckStatus.Down);
            Assert.Equal(MonitorStatus.Down, beat.HeartbeatStatus);
            Assert.False(beat.Important);
            Assert.Equal(NotifyKind.None, beat.Notify);
            Assert.Equal(EventAction.None, beat.EventAction);
        }
    }

    [Fact]
    public void Resend_while_down_re_alerts_every_nth_beat_without_reopening_the_event()
    {
        // retries:0 confirms Down on the first failure; resend:2 re-alerts every 2nd down-while-down beat.
        var sm = New(retries: 0, resend: 2);
        var open = sm.Evaluate(CheckStatus.Down);
        Assert.Equal(NotifyKind.Down, open.Notify);
        Assert.Equal(EventAction.Open, open.EventAction);

        // beat 1: no resend, beat 2: resend, beat 3: no resend, beat 4: resend
        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Down).Notify);
        var resend = sm.Evaluate(CheckStatus.Down);
        Assert.Equal(NotifyKind.ResendDown, resend.Notify);
        Assert.False(resend.Important);                 // a reminder, not a new transition
        Assert.Equal(EventAction.None, resend.EventAction); // the original event stays open
        Assert.Equal(NotifyKind.None, sm.Evaluate(CheckStatus.Down).Notify);
        Assert.Equal(NotifyKind.ResendDown, sm.Evaluate(CheckStatus.Down).Notify);
    }
}

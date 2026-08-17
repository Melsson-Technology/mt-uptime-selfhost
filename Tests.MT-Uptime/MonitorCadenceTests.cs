using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// Guardrails on how hard the engine is allowed to poll a target. The property that matters: a single
/// check can never outlive its own interval, because a check that overruns leaves PeriodicTimer with a
/// tick already pending — the next probe would then start with no gap, polling a slow target continuously.
/// </summary>
public class MonitorCadenceTests
{
    // --- Interval floor -------------------------------------------------------------------------

    [Theory]
    [InlineData(60, 60)]
    [InlineData(5, 5)]
    [InlineData(4, 5)]
    [InlineData(0, 5)]
    [InlineData(-30, 5)]
    public void ResolveInterval_never_goes_below_the_floor(int configured, int expected)
        => Assert.Equal(expected, (int)MonitorCadence.ResolveInterval(configured).TotalSeconds);

    // --- Timeout must stay strictly inside the interval -----------------------------------------

    [Fact]
    public void Timeout_comfortably_inside_the_interval_is_untouched()
        => Assert.Equal(30, Timeout(configured: 30, interval: 60));

    [Theory]
    [InlineData(60, 60)]  // equal to the interval
    [InlineData(90, 60)]  // longer than the interval — the case that caused back-to-back polling
    [InlineData(300, 10)]
    public void Timeout_at_or_beyond_the_interval_is_pulled_under_it(int configured, int interval)
    {
        var applied = Timeout(configured, interval);

        Assert.True(applied < interval, $"expected {applied}s to be shorter than the {interval}s interval");
        Assert.Equal(interval - 1, applied);
    }

    [Fact]
    public void Shortest_allowed_interval_still_leaves_a_usable_timeout()
    {
        // 5s interval: the timeout has to fit under it without collapsing to zero.
        var applied = Timeout(configured: 30, interval: MonitorCadence.MinIntervalSeconds);

        Assert.Equal(4, applied);
        Assert.True(applied >= 1);
    }

    // --- Absolute ceiling and floor -------------------------------------------------------------

    [Fact]
    public void Timeout_is_capped_even_when_the_interval_is_enormous()
        => Assert.Equal(MonitorCadence.MaxTimeoutSeconds, Timeout(configured: 600, interval: 86_400));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Nonpositive_timeout_becomes_one_second(int configured)
        => Assert.Equal(1, Timeout(configured, interval: 60));

    /// <summary>
    /// The invariant, asserted across the whole grid rather than at hand-picked points: whatever a row
    /// asks for, a check always finishes before its next tick is due.
    /// </summary>
    [Fact]
    public void Timeout_is_always_shorter_than_the_interval_for_any_configuration()
    {
        int[] intervals = [-10, 0, 5, 10, 30, 60, 300, 3_600, 21_600, 86_400];
        int[] timeouts = [-10, 0, 1, 5, 29, 30, 60, 299, 300, 5_000];

        foreach (var i in intervals)
        {
            var interval = MonitorCadence.ResolveInterval(i);
            foreach (var t in timeouts)
            {
                var applied = MonitorCadence.ResolveTimeout(t, interval);

                Assert.True(applied > TimeSpan.Zero,
                    $"interval {i}s / timeout {t}s produced a non-positive timeout ({applied})");
                Assert.True(applied < interval,
                    $"interval {i}s / timeout {t}s produced {applied}, which does not fit inside {interval}");
                Assert.True(applied.TotalSeconds <= MonitorCadence.MaxTimeoutSeconds,
                    $"interval {i}s / timeout {t}s exceeded the {MonitorCadence.MaxTimeoutSeconds}s ceiling");
            }
        }
    }

    // --- Save-time validation (the edit form's blocking check) ----------------------------------

    [Theory]
    [InlineData(60, 30)]
    [InlineData(60, 59)]
    [InlineData(5, 1)]
    [InlineData(21_600, 300)]
    public void ValidateCadence_accepts_a_timeout_inside_the_interval(int interval, int timeout)
        => Assert.Null(MonitorCadence.ValidateCadence(interval, timeout));

    [Theory]
    [InlineData(60, 60)]
    [InlineData(10, 30)]
    [InlineData(5, 5)]
    public void ValidateCadence_rejects_a_timeout_that_reaches_the_interval(int interval, int timeout)
    {
        var error = MonitorCadence.ValidateCadence(interval, timeout);

        Assert.NotNull(error);
        Assert.Contains("shorter than the interval", error);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateCadence_rejects_an_interval_below_the_floor(int interval)
    {
        var error = MonitorCadence.ValidateCadence(interval, timeoutSeconds: 1);

        Assert.NotNull(error);
        Assert.Contains("at least", error);
    }

    // --- Low-interval warning (non-blocking) ----------------------------------------------------

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(3_600)]
    public void No_warning_at_or_above_the_threshold(int interval)
        => Assert.Null(MonitorCadence.LowIntervalWarning(interval));

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(29)]
    public void Warns_below_the_threshold_without_blocking(int interval)
    {
        Assert.NotNull(MonitorCadence.LowIntervalWarning(interval));
        // The point of a warning rather than an error: it still saves.
        Assert.Null(MonitorCadence.ValidateCadence(interval, timeoutSeconds: 1));
    }

    [Fact]
    public void Warning_states_the_actual_rate()
        => Assert.Contains("12 checks a minute", MonitorCadence.LowIntervalWarning(5));

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public void No_warning_below_the_floor_because_validation_already_blocks(int interval)
        => Assert.Null(MonitorCadence.LowIntervalWarning(interval));

    // --- Slow detection (what the runner feeds the state machine) -------------------------------

    [Theory]
    [InlineData(2_001, 2_000)]
    [InlineData(8_240, 2_000)]
    [InlineData(2, 1)]
    public void A_response_over_the_threshold_is_slow(double responseMs, int threshold)
        => Assert.True(MonitorCadence.IsSlow(responseMs, threshold));

    [Theory]
    [InlineData(1_999, 2_000)]
    [InlineData(2_000, 2_000)]  // exactly at the threshold is not over it
    [InlineData(0, 2_000)]
    public void A_response_at_or_under_the_threshold_is_not_slow(double responseMs, int threshold)
        => Assert.False(MonitorCadence.IsSlow(responseMs, threshold));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void With_no_threshold_nothing_is_ever_slow(int? threshold)
    {
        // The default posture for every monitor: the feature is off, so even an awful response time
        // must not produce a degraded beat.
        Assert.False(MonitorCadence.IsSlow(999_999, threshold));
        Assert.False(MonitorCadence.IsSlow(0, threshold));
    }

    [Fact]
    public void A_probe_with_no_measured_response_time_is_never_slow()
    {
        // Absence of a measurement is not evidence of slowness — it would otherwise flag every checker
        // that doesn't record a duration.
        Assert.False(MonitorCadence.IsSlow(null, 2_000));
        Assert.False(MonitorCadence.IsSlow(null, null));
    }

    // --- Slow-response threshold ----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-100)]
    public void A_blank_or_zero_slow_threshold_means_the_feature_is_off(int? threshold)
        => Assert.Null(MonitorCadence.ValidateSlowThreshold(threshold, timeoutSeconds: 30));

    [Theory]
    [InlineData(2_000, 30)]
    [InlineData(1, 30)]
    [InlineData(29_999, 30)]
    public void A_threshold_below_the_timeout_is_accepted(int threshold, int timeout)
        => Assert.Null(MonitorCadence.ValidateSlowThreshold(threshold, timeout));

    [Theory]
    [InlineData(30_000, 30)]  // exactly the timeout — the check fails at the same instant
    [InlineData(45_000, 30)]
    [InlineData(5_000, 2)]
    public void A_threshold_at_or_above_the_timeout_is_rejected_as_unreachable(int threshold, int timeout)
    {
        var error = MonitorCadence.ValidateSlowThreshold(threshold, timeout);

        Assert.NotNull(error);
        Assert.Contains("below the timeout", error);
    }

    // --- Per-type defaults ----------------------------------------------------------------------

    [Fact]
    public void Tls_and_dns_default_to_far_longer_intervals_than_the_generic_type()
    {
        var generic = MonitorCadence.DefaultIntervalFor(MonitorType.Http);

        Assert.Equal(60, generic);
        Assert.Equal(21_600, MonitorCadence.DefaultIntervalFor(MonitorType.Tls));
        Assert.Equal(3_600, MonitorCadence.DefaultIntervalFor(MonitorType.Dns));
        Assert.Equal(3_600, MonitorCadence.DefaultIntervalFor(MonitorType.Push));
    }

    [Fact]
    public void Every_type_default_is_recognised_as_untouched()
    {
        foreach (var type in Enum.GetValues<MonitorType>())
            Assert.True(MonitorCadence.IsUntouchedDefault(MonitorCadence.DefaultIntervalFor(type)),
                $"{type}'s default interval is not recognised as an untouched default, so switching " +
                "away from it would silently discard the user's interval");
    }

    [Theory]
    [InlineData(45)]
    [InlineData(120)]
    [InlineData(7_200)]
    public void A_hand_typed_interval_is_not_treated_as_a_default(int interval)
        => Assert.False(MonitorCadence.IsUntouchedDefault(interval));

    [Fact]
    public void Every_type_default_survives_its_own_validation()
    {
        // A freshly-selected type must never land the user on a combination the form would reject.
        foreach (var type in Enum.GetValues<MonitorType>().Where(t => t != MonitorType.Push))
        {
            var interval = MonitorCadence.DefaultIntervalFor(type);
            Assert.Null(MonitorCadence.ValidateCadence(interval, timeoutSeconds: 30));
        }
    }

    private static int Timeout(int configured, int interval)
        => (int)MonitorCadence.ResolveTimeout(configured, TimeSpan.FromSeconds(interval)).TotalSeconds;
}

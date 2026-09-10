using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// <see cref="ProbeTimings"/> and the one claim built on top of it,
/// <see cref="ProbeTimingBreakdown.ConnectionReused"/>.
/// <para>
/// <b>Written from a wrong answer seen against a real host.</b> Driving the real checker at an
/// unresolvable name produced <c>total 31 ms (connection reused)</c>. The legs are written from
/// <c>ConnectCallback</c>, and a probe whose DNS lookup throws leaves that callback the same way a
/// pooled connection never enters it: with every leg null. <c>ConnectionReused</c> read that as "the
/// network setup path is ruled out, blame the server" on the one probe where DNS <em>was</em> the fault
/// — the same species of confidently-wrong alert line as <c>Last response code: 200</c> printed under
/// <c>Unexpected status 521</c>.
/// </para>
/// <para>
/// The fix is in <c>TimedConnectAsync</c>: each leg is recorded in a <c>finally</c>, so a leg that
/// failed still reports how long it took. What is pinned here is the resulting invariant — a probe that
/// got as far as recording any leg is not a reused connection.
/// </para>
/// </summary>
public class ProbeTimingsTests
{
    [Fact]
    public void A_probe_that_never_entered_the_connect_callback_is_a_reused_connection()
    {
        // Nothing recorded: the genuine pooled-connection case, and the only one this may claim.
        var breakdown = new ProbeTimings().ToBreakdown(totalMs: 42, ttfbMs: 40);

        Assert.True(breakdown.ConnectionReused);
        Assert.Null(breakdown.DnsMs);
        Assert.Null(breakdown.ConnectMs);
        Assert.Null(breakdown.TlsMs);
    }

    [Fact]
    public void A_failed_dns_lookup_is_not_a_reused_connection()
    {
        // The regression. DNS was attempted and threw; the leg is recorded regardless, which is what
        // stops the breakdown claiming the network path was never touched.
        var timings = new ProbeTimings();
        timings.RecordDns(TimeSpan.FromMilliseconds(31));

        var breakdown = timings.ToBreakdown(totalMs: 31, ttfbMs: null);

        Assert.False(breakdown.ConnectionReused);
        Assert.Equal(31, breakdown.DnsMs);
        Assert.Null(breakdown.ConnectMs);
    }

    [Fact]
    public void A_refused_connection_reports_how_long_the_connect_leg_took()
    {
        // "Refused after 20 s in the TCP connect" and "failed instantly at DNS" arrive as the same
        // message, and telling them apart is the whole point of the breakdown.
        var timings = new ProbeTimings();
        timings.RecordDns(TimeSpan.FromMilliseconds(1));
        timings.RecordConnect(TimeSpan.FromSeconds(20));

        var breakdown = timings.ToBreakdown(totalMs: 20_001, ttfbMs: null);

        Assert.False(breakdown.ConnectionReused);
        Assert.Equal(1, breakdown.DnsMs);
        Assert.Equal(20_000, breakdown.ConnectMs);
        Assert.Null(breakdown.TlsMs);
    }

    [Fact]
    public void A_handshake_that_never_started_records_no_tls_leg()
    {
        // RecordConnect is what arms the handshake clock. A connect that failed still records its leg,
        // so this asserts the arming is keyed on reaching the handshake rather than on the leg existing.
        var timings = new ProbeTimings();
        timings.RecordDns(TimeSpan.FromMilliseconds(2));
        timings.RecordHandshakeComplete();

        Assert.Null(timings.ToBreakdown(totalMs: 5, ttfbMs: null).TlsMs);
    }

    [Fact]
    public void A_plain_http_probe_records_a_tls_leg_of_about_nothing()
    {
        // The handshake is measured as the gap between connecting and the plaintext stream appearing,
        // so on plain HTTP it is ~0 rather than absent — and absent is what ConnectionReused reads.
        var timings = new ProbeTimings();
        timings.RecordDns(TimeSpan.FromMilliseconds(3));
        timings.RecordConnect(TimeSpan.FromMilliseconds(9));
        timings.RecordHandshakeComplete();

        var breakdown = timings.ToBreakdown(totalMs: 20, ttfbMs: 18);

        Assert.NotNull(breakdown.TlsMs);
        Assert.False(breakdown.ConnectionReused);
    }

    [Fact]
    public void The_ambient_slot_is_cleared_so_a_pooled_handler_cannot_write_into_a_finished_probe()
    {
        var timings = ProbeTimings.Begin();
        Assert.Same(timings, ProbeTimings.Current);

        ProbeTimings.End();
        Assert.Null(ProbeTimings.Current);

        ProbeTimings.End();   // idempotent by contract
        Assert.Null(ProbeTimings.Current);
    }
}

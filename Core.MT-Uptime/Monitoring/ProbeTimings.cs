using System.Diagnostics;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Collects the per-leg timings of one HTTP probe as the connection is established.
/// <para>
/// <b>Why an ambient value.</b> The legs are measured inside <c>SocketsHttpHandler</c>'s
/// <c>ConnectCallback</c> and <c>PlaintextStreamFilter</c>, which are properties of the <em>handler</em>
/// — one pooled handler serving every monitor — not of a request. There is no per-request state to hang
/// them on. An <see cref="AsyncLocal{T}"/> set by the checker immediately before <c>SendAsync</c> flows
/// into those callbacks along the request's async context, and because the callbacks mutate the object
/// rather than reassign the slot, the checker sees the results when the call returns.
/// </para>
/// <para>
/// <b>A probe that reuses a pooled connection never enters the callbacks at all</b>, so its legs stay
/// null. That is the correct answer, not a gap — see <see cref="ProbeTimingBreakdown.ConnectionReused"/>.
/// </para>
/// </summary>
public sealed class ProbeTimings
{
    private static readonly AsyncLocal<ProbeTimings?> Ambient = new();

    /// <summary>The collector for the probe running on this async context, if any.</summary>
    public static ProbeTimings? Current => Ambient.Value;

    private double? _dnsMs;
    private double? _connectMs;
    private double? _tlsMs;
    private long _connectedAtTicks;

    /// <summary>
    /// Starts collecting for the current async context. Dispose at the end of the probe so a pooled
    /// handler's later callbacks — for a different monitor — cannot write into this instance.
    /// </summary>
    public static ProbeTimings Begin()
    {
        var timings = new ProbeTimings();
        Ambient.Value = timings;
        return timings;
    }

    /// <summary>Clears the ambient slot. Safe to call more than once.</summary>
    public static void End() => Ambient.Value = null;

    public void RecordDns(TimeSpan elapsed) => _dnsMs = elapsed.TotalMilliseconds;

    public void RecordConnect(TimeSpan elapsed)
    {
        _connectMs = elapsed.TotalMilliseconds;
        _connectedAtTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Called once the plaintext stream is available, which is after any TLS handshake has completed.
    /// The handshake is not directly observable, so it is measured as the gap between the socket
    /// connecting and the plaintext stream appearing. On a plain-HTTP probe that gap is ~0, correctly.
    /// </summary>
    public void RecordHandshakeComplete()
    {
        if (_connectedAtTicks != 0)
            _tlsMs = Stopwatch.GetElapsedTime(_connectedAtTicks).TotalMilliseconds;
    }

    /// <summary>
    /// Assembles the breakdown. <paramref name="totalMs"/> and <paramref name="ttfbMs"/> are measured by
    /// the checker around <c>SendAsync</c>, since only it knows where the probe began and ended.
    /// </summary>
    public ProbeTimingBreakdown ToBreakdown(double totalMs, double? ttfbMs)
        => new(_dnsMs, _connectMs, _tlsMs, ttfbMs, totalMs);
}

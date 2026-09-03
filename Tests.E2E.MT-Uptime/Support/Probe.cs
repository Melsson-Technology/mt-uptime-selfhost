using System.Text.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests.E2E.Support;

/// <summary>
/// Builds a <see cref="MonitorContext"/> and runs a checker against it — the two lines every Tier 1
/// test would otherwise repeat, and the two places it is easy to be subtly wrong.
/// </summary>
public static class Probe
{
    /// <summary>
    /// Serialises a config object exactly as the product will read it back.
    /// <para>
    /// <b>The serializer options are the contract.</b> Every checker deserialises with a bare
    /// <c>JsonSerializer.Deserialize&lt;T&gt;(json)</c> — default options, so PascalCase property names,
    /// case-<em>sensitive</em> matching, and enums as integers. Hand-writing that JSON in each test
    /// would work right up until someone wrote <c>"url"</c> or <c>"Tls": "Required"</c>, at which point
    /// the field silently takes its default and the test passes while asserting nothing.
    /// </para>
    /// <para>
    /// Round-tripping the real config class with the same default options makes that impossible by
    /// construction: if a property is renamed, this and the checker move together.
    /// </para>
    /// </summary>
    public static string Json(object config) => JsonSerializer.Serialize(config, config.GetType());

    /// <summary>A context for one probe. The id and name only ever appear in messages at this tier.</summary>
    public static MonitorContext Context(
        MonitorType type,
        object config,
        TimeSpan? timeout = null,
        string? name = null)
        => new(1, name ?? $"e2e-{type}", type, timeout ?? TimeSpan.FromSeconds(10), Json(config));

    /// <summary>
    /// Runs a checker with a cancellation token that is guaranteed to fire.
    /// <para>
    /// <b>Not optional, and not belt-and-braces.</b> <c>MonitorContext.Timeout</c> is advisory for four
    /// of the six checkers: HTTP, TCP, DNS and TLS read it not at all, and rely on the token the runner
    /// passes. The monitoring engine's pooled probe clients carry <c>HttpClient.Timeout</c> of 100
    /// seconds, and a raw <c>TcpClient.ConnectAsync</c> against a blackholed port waits for the OS to
    /// give up — minutes. So a Tier 1 probe with <c>CancellationToken.None</c> against the blackhole
    /// target does not fail, it hangs, and the suite reports nothing at all.
    /// </para>
    /// <para>
    /// The database checkers are the exception: both pass <c>ctx.Timeout</c> to their driver's own
    /// connect timeout, so they self-limit. They still go through here, because a caller should not
    /// have to remember which two those are.
    /// </para>
    /// </summary>
    public static async Task<CheckResult> RunAsync(
        IMonitorChecker checker,
        MonitorContext ctx,
        TimeSpan? cancelAfter = null)
    {
        using var cts = new CancellationTokenSource(cancelAfter ?? TimeSpan.FromSeconds(20));
        return await checker.CheckAsync(ctx, cts.Token);
    }

    /// <summary>
    /// Asserts that a probe is cancelled rather than returning a result.
    /// <para>
    /// This is the corrected form of a prediction the plan got wrong. Every checker ends with
    /// <c>catch (OperationCanceledException) { throw; }</c> — deliberately, so the runner can tell a
    /// per-check timeout apart from application shutdown. The familiar <c>Down("Timeout")</c> is
    /// <c>MonitorRunner</c>'s message, produced one layer up; no checker ever returns it. A Tier 1 test
    /// expecting a Down result from a blackholed target would therefore fail on the exception, and the
    /// obvious "fix" — catching it and calling that a pass — would delete the assertion.
    /// </para>
    /// </summary>
    public static async Task AssertCancelledAsync(
        IMonitorChecker checker,
        MonitorContext ctx,
        TimeSpan cancelAfter)
    {
        using var cts = new CancellationTokenSource(cancelAfter);
        var e = await Record.ExceptionAsync(() => checker.CheckAsync(ctx, cts.Token));

        Assert.NotNull(e);
        Assert.True(
            e is OperationCanceledException,
            $"expected the probe to be cancelled, but it threw {e.GetType().Name}: {e.Message}");
    }
}

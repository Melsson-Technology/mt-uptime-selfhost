namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// The raw result of a single probe. Checkers return Up/Down only — the retry/pending logic and
/// state transitions are the engine's job (see <see cref="MonitorStateMachine"/>).
/// </summary>
public sealed record CheckResult(
    CheckStatus Status,
    double? ResponseTimeMs,
    string? StatusCode,
    string? Message,
    DateTime? CertExpiresAt = null,
    bool Hard = false)
{
    /// <summary>
    /// Hard cap on <see cref="Message"/>, applied by the factories below so every checker inherits it.
    /// <para>
    /// A check message is the one field on this record that a <em>monitored target</em> controls: it
    /// carries DNS answers, HTTP bodies and driver error text from a host that is by definition outside
    /// the trust boundary — often the very host whose operator would rather not be alerted about. It is
    /// then persisted on every heartbeat, held in memory per monitor for the dashboard, and pasted into
    /// the outbound alert body.
    /// </para>
    /// <para>
    /// Uncapped, a verbose or hostile target could push tens of kilobytes through all three. The sharp
    /// end is not storage: it is that Telegram, Discord and Slack all reject an oversized payload, so a
    /// target able to inflate this <b>suppresses the Down alert about its own outage</b> — the failure
    /// mode a monitoring system least wants. 1 KB is far more than any legible diagnostic needs and far
    /// less than any channel's limit.
    /// </para>
    /// </summary>
    public const int MaxMessageLength = 1024;

    public static CheckResult Up(double responseMs, string? statusCode = null, string? message = null, DateTime? certExpiresAt = null)
        => new(CheckStatus.Up, responseMs, statusCode, Truncate(message), certExpiresAt);

    /// <summary>
    /// Clamps target-controlled text to <see cref="MaxMessageLength"/>, marking the cut so a reader can
    /// tell a truncated message from a short one.
    /// </summary>
    public static string? Truncate(string? message)
        => message is not null && message.Length > MaxMessageLength
            ? string.Concat(message.AsSpan(0, MaxMessageLength), "… (truncated)")
            : message;

    /// <summary>
    /// A failed probe. Set <paramref name="hard"/> when the failure is a <em>definitive</em> negative
    /// answer from the target (e.g. the server replied with a bad HTTP status) — the state machine
    /// then confirms Down immediately instead of waiting out the retry window. Leave it false for
    /// transient/unreachable failures (timeouts, connection errors, DNS), which keep the retry cushion.
    /// </summary>
    public static CheckResult Down(string message, double? responseMs = null, string? statusCode = null, DateTime? certExpiresAt = null, bool hard = false)
        => new(CheckStatus.Down, responseMs, statusCode, Truncate(message)!, certExpiresAt, hard);
}

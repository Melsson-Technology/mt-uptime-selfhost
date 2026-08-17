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
    public static CheckResult Up(double responseMs, string? statusCode = null, string? message = null, DateTime? certExpiresAt = null)
        => new(CheckStatus.Up, responseMs, statusCode, message, certExpiresAt);

    /// <summary>
    /// A failed probe. Set <paramref name="hard"/> when the failure is a <em>definitive</em> negative
    /// answer from the target (e.g. the server replied with a bad HTTP status) — the state machine
    /// then confirms Down immediately instead of waiting out the retry window. Leave it false for
    /// transient/unreachable failures (timeouts, connection errors, DNS), which keep the retry cushion.
    /// </summary>
    public static CheckResult Down(string message, double? responseMs = null, string? statusCode = null, DateTime? certExpiresAt = null, bool hard = false)
        => new(CheckStatus.Down, responseMs, statusCode, message, certExpiresAt, hard);
}

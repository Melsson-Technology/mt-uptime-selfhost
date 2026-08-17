using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Logs webhook deliveries without their URL.
/// <para>
/// For Slack, Telegram and generic webhooks the URL <em>is</em> the credential — the token sits in the
/// path (<c>hooks.slack.com/services/T…/B…/…</c>, <c>api.telegram.org/bot&lt;token&gt;/…</c>). The default
/// <c>IHttpClientFactory</c> logging records the full request URI at Information level, so every
/// delivery would write a live credential into the system log in plaintext. That silently undoes
/// <see cref="Security.ISecretProtector"/>, which exists precisely to keep these values encrypted at
/// rest: encrypted in the database, then printed in the clear on the way out.
/// </para>
/// <para>
/// Host, status code and timing are kept, because those are what you actually need to tell "Slack
/// rejected it" from "we never reached Slack". Only the path and query are dropped.
/// </para>
/// </summary>
internal sealed class RedactingHttpClientLogger(ILogger<RedactingHttpClientLogger> log) : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request)
    {
        log.LogInformation("Notification POST to {Host} (path withheld — it carries the credential)",
            request.RequestUri?.Host ?? "unknown");
        return null;
    }

    public void LogRequestStop(
        object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
    {
        // A non-success status is the interesting case: a wrong or revoked webhook answers 403/404, and
        // without this line the only symptom is a notification that quietly never arrives.
        var level = response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning;
        log.Log(level, "Notification POST to {Host} returned {StatusCode} after {ElapsedMs:0}ms",
            request.RequestUri?.Host ?? "unknown", (int)response.StatusCode, elapsed.TotalMilliseconds);
    }

    public void LogRequestFailed(
        object? context, HttpRequestMessage request, HttpResponseMessage? response,
        Exception exception, TimeSpan elapsed)
    {
        // Log the exception's message rather than the exception, whose ToString() can include the
        // request URI on some transport failures.
        log.LogError("Notification POST to {Host} failed after {ElapsedMs:0}ms: {Error}",
            request.RequestUri?.Host ?? "unknown", elapsed.TotalMilliseconds, exception.Message);
    }
}

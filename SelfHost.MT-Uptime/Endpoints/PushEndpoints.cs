using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Web.Endpoints;

/// <summary>
/// Inbound heartbeat endpoint for push monitors. The token in the URL is the only credential, so the
/// route is anonymous. GET/POST/HEAD are all accepted so any cron/curl/wget/PowerShell one-liner works.
/// </summary>
public static class PushEndpoints
{
    /// <summary>Name of the per-client-IP rate-limit policy applied to the ping route (configured in Program.cs).</summary>
    public const string RateLimitPolicy = "push-ping";

    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods($"/{PushMonitorManager.PingPathSegment}/{{token}}", ["GET", "POST", "HEAD"],
            (string token, PushMonitorManager push) =>
                push.RecordPing(token) ? Results.Text("OK") : Results.NotFound("Unknown ping token"))
           .RequireRateLimiting(RateLimitPolicy)
           // Exempt from the authenticated-by-default fallback policy: the token in the URL *is* the
           // credential, and the cron jobs that call this have no session to present.
           .AllowAnonymous();
    }
}

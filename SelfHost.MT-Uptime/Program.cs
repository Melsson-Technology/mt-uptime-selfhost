using MT.Uptime.Web.Hosting;

// The self-hosted server: one instance, one SQLite database, owned entirely by whoever installed it.
//
// Everything this actually does lives in MtUptimeHosting — storage, the monitoring engine, cookie
// authentication with session-stamp revocation, the authorization policies, rate limiting, the security
// headers and the endpoint map. It is written there rather than here because a second composition root
// (a host running several instances against one database, say) needs the identical pipeline, and a
// copied pipeline drifts away from the next security fix in silence.
//
// The defaults are this deployment, so there is nothing to configure here: SQLite beside the app, the
// first-run setup wizard, and the database administration endpoints an operator needs for their own
// instance.
var builder = WebApplication.CreateBuilder(args);

builder.AddMtUptime();

var app = builder.Build();

await app.UseMtUptimeAsync();

await app.RunAsync();

/// <summary>
/// Top-level statements generate an internal Program class; this makes it public so the test project can
/// boot the real pipeline through <c>WebApplicationFactory&lt;Program&gt;</c>. Endpoint authorization can
/// only be verified against the actual middleware chain, not by unit-testing a handler.
/// </summary>
public partial class Program;

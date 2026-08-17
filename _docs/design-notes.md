# Design notes

Decisions that are not obvious from the code, and problems that cost real time to diagnose. If you are
about to change something here and it looks unnecessarily convoluted, read the relevant entry first —
most of these look wrong until you know what they are working around.

---

## Monitoring

### HTTP probes must send a User-Agent

A bare `HttpClient` request sends **no** `User-Agent` header at all. Many sites and most WAFs answer a
UA-less request with **403**, which the engine correctly reads as a definitive failure — so the monitor
reports the site down while a browser shows it working perfectly.

This was diagnosed against a WAF-fronted site that returned 403 to every probe and 200 to any request
with a UA, however arbitrary. `HttpChecker.UserAgent` is applied to all three named clients in
`AddMonitoringEngine`. Do not remove it, and prefer keeping it identifiable — a site owner who sees your
probes in their logs should be able to find out what they are.

### The engine never polls harder when a target is failing

`MonitorStateMachine` counts failures; the runner keeps its fixed `PeriodicTimer` cadence in every state.
Plenty of monitoring tools shorten the interval during retries, which is precisely how a monitor turns a
struggling server into a dead one. Combined with the sequential loop (each check completes before the
next tick is honoured), a monitor can never have more than one request in flight against its target.

### A check's timeout is always shorter than its interval

`MonitorCadence.ResolveTimeout` enforces this, and the edit form rejects the combination up front.
Without it, a check that runs longer than its interval leaves `PeriodicTimer` with a tick already pending,
so the next probe starts with **no gap at all** — continuous hammering of a target that is by definition
already slow.

The same file holds the 5-second interval floor and the per-type defaults. TLS defaults to 6 hours and
DNS to 1 hour because certificate expiry and DNS records change on the order of days; probing them every
60 seconds is thousands of pointless TLS handshakes a day against someone else's infrastructure.

### Degraded counts as available

A monitor over its response-time threshold reports `Degraded`, and `MonitorStatsService` counts that as
**up** for uptime percentage — the target did answer, correctly, just slowly. Counting it as downtime
would mean enabling the feature retroactively wrecked your numbers, which would teach everyone to leave
it off.

---

## Storage

### SQLite has one writer, so the engine has one writer

Runners push results into a channel and `HeartbeatWriter` drains it one at a time. Concurrent checks
would otherwise contend on SQLite's single write lock and start failing with "database is locked" under
load. Never write to the database from a checker.

UI writes are separate and rely on WAL plus `busy_timeout=5000` to ride out any brief contention.

### Connection pooling holds the file open after Dispose

`Microsoft.Data.Sqlite` returns connections to a pool rather than closing them, so the file handle
survives `Dispose`. The backup endpoint therefore sets `Pooling=false` on its throwaway connections —
without that, the temp file is still locked when it is streamed and you get
`IOException: being used by another process`.

Also: **do not open a WAL database read-only for the backup source.** It appears to work, right up until
the WAL is non-empty, and then it fails. Use the default read-write mode; the online-backup API only
reads anyway.

### Rollups run before pruning

`RetentionService` rolls raw heartbeats into hourly and daily buckets *and then* deletes the raw rows,
so no bucket is ever lost. The rollup watermark is `MAX(BucketStart)` per period, which makes re-runs
idempotent. `MonitorStatsService` reads raw when it still covers the window and stitches daily rollups to
surviving raw when it doesn't, seamed on a day boundary so nothing is double-counted.

### Losing the Data Protection keys is unrecoverable

Secrets are encrypted before they reach the database, with keys stored outside it. A stolen database
alone yields nothing — but a restored database without its keys is equally useless, and the failure mode
is silent: the app starts, the settings page renders, and every stored secret is simply gone. Back up the
key directory with the database, never separately.

Never change the purpose string in `DataProtectionSecretProtector`. It is part of the key derivation, so
changing it makes every existing installation's secrets undecryptable.

---

## Web layer

### Auth pages are static SSR, not interactive

`SignInAsync` writes response headers, which cannot happen inside a Blazor interactive circuit. So the
login, setup, profile and password-reset pages are static SSR and post real HTML forms to minimal
endpoints under `/auth/*`. Feedback comes back through the query string, which is why those pages read
`[SupplyParameterFromQuery]`.

The routes are `/auth/login` rather than `/login` for a duller reason: an endpoint sharing a path with a
Blazor page route throws `AmbiguousMatchException`.

### Endpoints are authenticated by default

An authorization fallback policy requires an authenticated user, and public endpoints opt out explicitly
with `.AllowAnonymous()`. This is not belt-and-braces — an earlier build shipped `/auth/profile` with no
authorization at all, which allowed full account takeover in two requests, and a default-deny policy is
what makes that class of mistake impossible rather than merely unlikely.

Note the Razor component endpoints deliberately opt out: page access is enforced by each component's own
`[Authorize]`/`[AllowAnonymous]` attribute through `AuthorizeRouteView`. Applying the fallback there too
would break the anonymous pages and the circuit negotiate.

### Password reset must not reveal whether an account exists

`/auth/forgot` returns an identical response whether the address matches an account, whether email is
configured, and whether delivery succeeded. Anything else turns it into an account-enumeration oracle.
The cost is that a broken mail setup is invisible from the browser, so the endpoint **logs an error** when
sending fails — the server log is the only place an operator can discover that resets do not work.

### SVG coordinates need InvariantCulture

The inline charts write coordinates into SVG attributes, and SVG requires `.` as the decimal separator.
On a machine with a comma decimal separator, string interpolation silently produces invalid path data and
the chart renders as nothing. See `ResponseChart.razor`.

---

## Notifications

### A webhook URL is a credential, so it is never logged

For Slack, Telegram and generic webhooks the token lives in the URL path
(`hooks.slack.com/services/T…/B…/…`, `api.telegram.org/bot<token>/…`). Anyone holding that URL can post as
you. MT-Uptime therefore stores it encrypted through `ISecretProtector`, like any other secret.

That encryption is trivially undone by default framework behaviour: `IHttpClientFactory` logs the full
request URI at Information level, so every delivery writes a live credential into the system log in
plaintext. Encrypted at rest, printed in the clear on the way out.

The notification client therefore calls `RemoveAllLoggers()` and substitutes `RedactingHttpClientLogger`,
which keeps host, status code and timing but drops path and query. Those three are what distinguish "the
provider rejected it" from "we never reached the provider" — the diagnostic value is in the status, not
the URL. A non-success status logs at **Warning**, because a revoked webhook returns 403 and is otherwise
completely invisible: the only symptom is an alert that never arrives.

The failure path logs `exception.Message` rather than the exception, since some transport exceptions
include the request URI in `ToString()`.

The general lesson is worth stating plainly: **if a secret is in a URL, assume every layer that touches
URLs will log it** unless you have checked otherwise.

## Deployment

### Installing a runtime can remove the one other applications are using

On Ubuntu 24.04, `apt-get install aspnetcore-runtime-10.0` removes `dotnet-host-8.0` — the package that
provides `/usr/bin/dotnet`. On a machine hosting other .NET applications, that is their `ExecStart`.
"Runtimes install side-by-side" is true of the runtimes and not of the host package.

So on any shared host, **simulate first**:

```bash
apt-get install -s -y aspnetcore-runtime-10.0 | grep '^Remv'
```

If that prints anything, do not install. Build self-contained instead
(`./scripts/build-and-package.sh --self-contained`): MT-Uptime then carries its own runtime, touches no
system packages, and costs about 50 MB instead of 5. The trade is that runtime security patches arrive
only when you rebuild, rather than through the distribution.

The same caution applies to `apt-get install nginx` on a machine already serving other sites — if the
index has a newer version, that upgrades and restarts it.

### A self-contained build loses its execute bit on Windows

The native apphost is packaged `644` when built from Windows, because NTFS has no execute bit. systemd
then fails with a bare "Permission denied", which is an unhelpful way to discover a file-mode problem.
`deploy-on-server.sh` chmods it after unpacking.

## Dependencies

### SQLitePCLRaw is pinned

EF Core 10.0.10 pulls `SQLitePCLRaw.bundle_e_sqlite3` 2.1.11, whose bundled SQLite predates 3.50.2 and so
trips NU1903 (CVE-2025-6965). Bundle 2.1.12 moves the native library to SQLite 3.53.3. The pin lives in
`Core.MT-Uptime.csproj`; drop it once EF Core references a patched transitive itself.

### MySqlConnector, not MySql.Data

`MySqlConnector` is MIT. Oracle's `MySql.Data` is GPL-2.0 with a FOSS exception, which would complicate
both the licence story and any commercial use. This is a deliberate choice — please do not "fix" it by
swapping in the official driver.

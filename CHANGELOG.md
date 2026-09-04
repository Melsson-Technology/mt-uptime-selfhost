# Changelog

Notable changes to MT-Uptime. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning will follow [Semantic Versioning](https://semver.org/) from 1.0.0 onward.

## [Unreleased]

First public release. Everything below describes the state at open-sourcing rather than a delta from a
previous published version.

### Monitoring

- Seven monitor types: HTTP(S), TCP, DNS, MySQL, PostgreSQL, TLS certificate expiry, and passive
  push/heartbeat.
- Authenticated HTTP monitoring: HTTP Basic or Bearer token, arbitrary request headers, a request body
  with its own content type, and a per-monitor User-Agent override for targets whose WAF allowlists one.
  The password or token and the header block are encrypted at rest with the same Data Protection key ring
  as every other stored secret; a credential that will not decrypt reports that plainly instead of
  sending an unauthenticated request and blaming the target for the 401 that comes back.
- Retry windows before an outage is confirmed, with definitive failures (a bad HTTP status) skipping the
  wait since retrying cannot change the answer.
- Slow-response alerting: a successful but over-threshold check reports **Slow**, confirmed only after N
  consecutive slow checks. Counts as available for uptime percentage — the target did answer.
- Guardrails on how hard a target may be polled: a 5-second interval floor, a check timeout always kept
  shorter than its interval so checks cannot run back-to-back, and long default intervals for TLS (6h)
  and DNS (1h), whose answers change on the order of days.
- Inverted ("upside down") monitors, for endpoints that should *not* respond.
- **Correlated incidents.** When a host carrying several monitored targets fails, MT-Uptime opens **one**
  incident rather than one alert per target. Each monitor is resolved to the address it actually depends
  on — so twenty sites on one box correlate even though they share no hostname — and failures on that
  address within a short window join the same incident. Correlation is inferred infrastructure and is
  deliberately separate from tags: a tag says whose a monitor is, the correlation key says what it ran on.
  Monitors that cannot be resolved to anything (push monitors, DNS monitors on the system resolver) simply
  get an incident of their own, so there is no special case to reason about.
- **Acknowledge and snooze.** Acknowledging an incident stops the repeat-while-down alerts for every
  monitor in it until it recovers; snoozing does the same for a fixed period. Both are per-incident and
  never per-monitor, so acknowledging a dead host also covers the next monitor on it to fail rather than
  silencing one alert out of twenty. **A recovery notification is never suppressed** — channels that hold
  state, PagerDuty above all, would otherwise be left with a remote incident open and no way to close it.
- **The alert itself tells you it is a correlated outage.** A page for one of twenty monitors on a dead
  host reads "acme-web is DOWN (+19 more)" and lists what else is affected and which address they share,
  rather than arriving as the twentieth identical-looking alert in a minute. A single-monitor outage says
  nothing about incidents at all, so the extra lines only appear when they mean something.
- **Alerts carry enough context to say what broke**: the address the target resolved to, the last response
  code, the last few response times so a slow decline is visible, and the certificate expiry — but only
  when it is within thirty days or already past, since a certificate good for another nine months is not
  a clue. Structured consumers get the same detail as nested `incident` and `diagnostics` objects on the
  webhook payload and in PagerDuty custom details; existing webhook fields are unchanged.
- **Maintenance windows.** One-off or repeating, scoped to individual monitors, to tags, or to the whole
  instance. Repeating windows are scheduled by wall-clock in a time zone you choose, so "Sundays at 02:00"
  stays at 02:00 across daylight-saving changes. During a window, failures do not alert — and the affected
  checks are left out of the uptime percentage entirely, counted as neither up nor down. **What was
  recorded does not change:** a monitor that really was down still shows as down in its history and its
  event log. Suppressing an alert is not the same as editing the number the product is trusted for.
- **Tags.** Label monitors by environment, customer or host, and filter the dashboard by them — with an
  "Untagged" filter for finding what you have not labelled yet. Tag names are unique case-insensitively,
  so "Prod" and "prod" cannot become two tags that each match half your monitors. Deleting a tag
  unassigns it everywhere and leaves the monitors alone.

### Accounts

- **Users and roles.** **Admin** manages everything including accounts, settings, backup and export;
  **Editor** manages monitors, notification channels and status pages; **Viewer** is read-only. Admins
  add accounts on the Users page with an initial password to hand over, and can set a password for
  someone who has lost theirs — useful on installs where email was never configured.
- **Usernames are case-insensitive at sign-in.** An account created as "Matt" accepts "matt". Previously
  SQLite's default binary collation rejected it and the page reported "Invalid username or password",
  which points at the password when the username is the problem. The unique index is case-insensitive to
  match, so "Matt" and "matt" can no longer be two accounts — telling users apart by capitalisation alone
  is a phishing surface, not a feature. Upgrading an install that already holds such a pair will stop at
  this migration; rename one first.
- The last remaining Admin cannot be demoted or deleted, and nobody can delete their own account. Both
  are enforced in the service rather than the UI, because the failure they prevent — an instance with no
  administrator — cannot be repaired through the application at all.
- Upgrading an existing install keeps its accounts as administrators. The role column defaults to Viewer
  so that a new account is never accidentally privileged; the migration explicitly promotes rows that
  predate it.

### Alerting

- Nine notification channels, global or per monitor: email (SendGrid), Slack, **Discord**,
  **Microsoft Teams**, Telegram, **ntfy**, **Gotify**, **PagerDuty** and a generic webhook.
- **PagerDuty is stateful, not another message feed.** A recovery sends `resolve` against the same
  `dedup_key` the outage opened, so the incident closes and the escalation stops; repeat-while-down
  alerts deduplicate against that incident instead of multiplying.
- Teams uses the **Adaptive Card** format that Power Automate Workflows expects, not the Office 365
  connector `MessageCard` that Microsoft is retiring.
- Severity is mapped once, centrally, and each channel renders it in its own vocabulary — an embed
  colour, a card colour, an ntfy or Gotify priority, a PagerDuty severity. Previously each channel
  switched on the alert kind itself, which is how a **Degraded** alert once shipped wearing Slack's
  "information" icon.
- Optional resend-while-down at a configurable cadence.
- Password reset by email, with single-use tokens stored only as SHA-256 hashes and a one-hour expiry.

### Visualisation

- Live dashboard pushed over the Blazor circuit — no polling.
- Inline SVG heartbeat bars and response-time charts, no JavaScript charting library.
- Public status pages at `/status/{slug}` with 30-day uptime.
- **Status pages communicate during an incident**, rather than only listing monitors. Affected services,
  scheduled maintenance and operator updates (Investigating / Identified / Monitoring / Resolved) appear
  automatically; resolved incidents stay up for a week so a reader arriving late still sees what happened.
  Incidents are published by default and can be hidden individually — a status page that stays green
  through an outage its own monitors are reporting is worse than no status page.
- **A status page only ever names the monitors it lists.** Because incidents group monitors by shared
  infrastructure, one incident can span several customers on one host. Each page is rendered from its own
  monitor list, so an outage on a shared box cannot disclose to one customer that another exists.

### Deployment

- **Dockerfile** targeting `linux/amd64` and `linux/arm64`, with a Compose file that builds it. It is
  multi-stage, so Docker is the only prerequisite. **No image is published to a registry yet** — the
  Compose file builds from source, and any `mt-uptime:latest` reference is aspirational until then.
  One volume covers
  `/var/lib/mt-uptime`, holding the database and the Data Protection keys together — mounting only the
  database yields a container that starts, reports healthy, and cannot decrypt any stored secret.
- Cross-platform build scripts (`.sh` and `.ps1` pairs) plus server-side provision and deploy scripts,
  with an atomic publish swap that keeps the previous build for a one-command rollback.
- `--self-contained` / `-SelfContained` build option, which bundles the .NET runtime so the target needs
  no runtime installed. Intended for shared hosts, where installing an ASP.NET runtime can remove the
  `dotnet-host` package other applications depend on.
- Per-host configuration through an optional systemd `EnvironmentFile`, so the listening port and storage
  paths can be changed without editing the shipped unit. The default is 5081 rather than the ASP.NET Core
  default of 5000, which is frequently already in use on a shared host.

### Testing

- **An end-to-end battery, in `e2e/` and `Tests.E2E.MT-Uptime/`** — built, and run green on a real box.
  The existing suite is hermetic by design, which is a promise worth keeping and also a limit: none of
  its 371 tests has ever watched a real service go down. `e2e/install-targets.sh` prepares a disposable
  Ubuntu box with a real target behind every monitor type — an HTTP fixture behind nginx on plain HTTP
  and on four HTTPS ports carrying valid, near-expiry, expired and untrusted certificates; a TCP
  listener, a closed port and a blackholed one; an authoritative DNS zone with A/AAAA/CNAME/MX/TXT
  records; and MySQL and PostgreSQL with TLS from a CA minted at install time. A root-owned helper
  breaks and restores each target on demand, and blocks until the change is observable from outside, so
  a test never races the outage it just asked for.
- The battery is a **separate project, kept out of `MT-Uptime.Engine.slnx`**, so `scripts/test.sh` still
  runs exactly 371 hermetic tests and the "works on a fresh clone with only the SDK" promise is
  unchanged. Without a target manifest every end-to-end test reports skipped rather than failed, so it
  is safe to run anywhere.
- **`e2e/smoke.sh` proves the documented install actually works**, against the service on its own port
  and through nginx: health on both origins, the one-shot first-run wizard and its setup token, the
  anonymous boundaries including the Blazor circuit's, the push endpoint, both rate limiters, the admin
  backup and JSON export, and the state directory's permissions and key ring. It also completes first-run
  setup and records the administrator, which is what lets the browser tier sign in.
- `e2e/run-tests.sh` runs one tier at a time and refuses rather than reporting a hollow pass: a missing
  or unreadable target manifest, and a tier whose filter matches no test, are both errors — `dotnet test`
  exits zero when its filter matches nothing.
- **The checker matrix: 114 tests putting every monitor type against a real service.** Tcp, Dns and Tls
  had no behavioural tests at all before this, and `HttpCheckerTests` drove a stubbed message handler —
  which cannot observe the four pooled clients the monitoring engine registers, and those clients are
  where "follow redirects" and "ignore TLS errors" actually live. Among the things now pinned against
  real servers: a bad status code confirms Down immediately while a missing keyword does not; ignoring
  TLS errors does not quietly re-enable redirect-following; a redirect loop reports the final 3xx rather
  than throwing; every database TLS mode up to `VerifyCa` really does verify against the system trust
  store; and a DNS resolver that is not a valid IP address silently falls back to the system resolver.
- **21 pipeline scenarios that run the whole engine against a real outage.** A target is broken, and
  the assertion is made on what came out the far end: heartbeats, incidents and a webhook delivered to
  an endpoint the tests host. Among them — a bad HTTP status confirms Down on the first check while a
  refused socket spends its retry window first, and the beats in between are recorded as Pending and
  alert exactly once; a blip that recovers inside that window never alerts at all; a sustained slowdown
  confirms Degraded and a single fast check clears the streak; a push monitor goes down on silence and
  recovers on a ping; adding a healthy monitor pages nobody; and two monitors failing on one host open
  **one** correlated incident rather than two alerts.
- **A browser tier that drives the installed instance through every configuring page.** Every one of
  them is `@rendermode InteractiveServer`, so until now none had ever been exercised: a monitor of each
  of the seven types created through the real form, a notification channel proved with its own "Send
  test" button, roles enforced for an Editor and a Viewer, a status page read by an anonymous visitor,
  tag filtering, and a maintenance window that suppresses the outage page while still announcing the
  recovery. The one that matters most is the dashboard flipping to **Down without a reload** — a dead
  Blazor circuit renders a perfect page that never changes again, and nothing but a browser can tell
  the difference.
- No certificates are committed. `scripts/publish-public.sh` refuses to publish a `.crt`, `.key` or
  `.pem` tracked under `engine/`, so the whole set is minted at runtime — into a staging directory that
  is swapped in with a rename, because the first version deleted the old certificates before writing
  the new ones and an interrupted run left the box with none at all.
- **The battery has run, on a disposable Ubuntu 24.04 box: targets 50/50, Tier 0 36/36, Tier 1
  114/114, Tier 2 21/21, Tier 3 18/18.** It found eighteen defects in the battery itself and three in
  the product — one fixed (below), one open (an unknown status-page slug answers 200 rather than 404),
  and one recorded as a documented limitation with the experiment that would confirm it: MySQL
  `VerifyFull` will not connect to a server that presents its CA in the handshake, where `VerifyCa`
  will, MySQL's own client at `VERIFY_IDENTITY` will, and Npgsql's `VerifyFull` will against a
  certificate from the same CA.

### Storage

- SQLite with WAL, tuned pragmas, and incremental auto-vacuum.
- Raw heartbeats rolled into hourly and daily buckets before pruning, so long-range uptime survives on a
  small disk.
- Authenticated database backup and monitor export (secrets redacted).
- Secrets encrypted at rest via ASP.NET Core Data Protection.

### Security

- **The first-run setup wizard requires a one-time token.** It is printed to the log on first boot and
  written to `setup-token` in the data directory (mode 0600), and destroyed once the administrator
  account is created. The wizard mints an Admin and cannot require a login, so without this the only
  thing standing between a passer-by and ownership of a new instance was arriving second — and the
  redirect to `/setup` advertises the window to anyone who looks. It also makes the documented
  account-recovery procedure (delete the rows, redo the wizard) safe to run on a live host.
- **Sessions can be revoked.** Deleting an account, setting or changing its password, and changing its
  role now end that account's existing sessions immediately. The auth cookie carries a session stamp that
  is re-checked on every request and every 30 seconds inside an open interactive circuit. Previously the
  cookie was entirely self-contained: nothing re-read the account row, so "Delete" and "Set password" —
  the two remedies the UI offers — left the holder's session working until it expired. A demotion now
  takes effect at the moment it is made rather than at the demoted user's next sign-in.
- **A read-only Viewer is no longer shown a push monitor's ping URL.** The token in that URL is a bearer
  credential: anyone holding it can record an Up beat anonymously, pinning a monitor healthy and
  suppressing the outage alert it exists to send. It is now built only for Editors and Admins, and never
  enters the page at all for anyone else.
- **Push tokens can be rotated.** The monitor editor grows a "Regenerate token" button; the previous URL
  stops working on save. There was previously no way to withdraw a leaked ping URL short of deleting the
  monitor and losing its history.
- **Emailed links come from a configured origin, not the request.** Set `App__PublicBaseUrl` to the
  instance's public address. Password-reset links were built from the `Host` header, which the caller
  controls, so someone who knew an account's email address could cause a genuine reset email whose link
  pointed at a host they owned. Setting it also narrows `AllowedHosts` from `*` to that hostname, so
  forged `Host` headers are rejected before reaching any handler.
- **Security response headers on every response** — a Content-Security-Policy with `script-src 'self'`,
  plus `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` and `Permissions-Policy`. The
  script directive is the one that earns its keep: it means an HTML injection anywhere in the app cannot
  become script execution. It is only affordable because no inline script remains — the two
  copy-to-clipboard handlers moved into `wwwroot/js/copy-field.js`, and `<ImportMap />` was dropped (see
  the note in `App.razor` before adding a collocated `.razor.js`). Inline *styles* are still permitted:
  tag chips colour themselves through a style attribute, and that colour is validated to `#RRGGBB`.
- **The Blazor circuit endpoint requires authentication.** `/_blazor` and its negotiate endpoint were
  anonymous, so a caller with no account could open and hold WebSocket connections indefinitely. Every
  anonymous page in this app is static SSR, so none of them needs a circuit. `/_blazor/initializers`
  stays anonymous because `blazor.web.js` fetches it on every page load.
- **`X-Forwarded-For` is trusted from loopback only**, which is exactly the documented nginx deployment.
  It was previously trusted from any source — correct behind that proxy and wrong everywhere else,
  including the Docker default. A proxy that is not on loopback is declared with
  `ForwardedHeaders__KnownProxies`; a malformed value stops the app rather than silently trusting
  nothing. **Docker Compose now publishes on `127.0.0.1` rather than every interface**, because Kestrel
  serves plain HTTP.
- **Public status pages are cached for 15 seconds and no longer rebuild per request.** `/status/{slug}`
  is anonymous and cost one 30-day heartbeat aggregation per monitor — over a second of SQLite for a
  20-monitor page, with the page refreshing itself every 60 seconds and nothing rate-limiting it.
- **`/admin/backup` stages its copy inside the state directory rather than the shared `/tmp`**, and sets
  the file owner-only. The temporary file is a complete database, and `/tmp` is world-readable.
- **Failed sign-ins no longer trust `X-Real-IP`** for the logged client address — it is taken from the
  connection, which `UseForwardedHeaders` has already resolved. The header is caller-controlled, so
  every logged address was attacker-chosen, and an operator pointing fail2ban at that field would have
  been handing out a remote ban primitive. The submitted username is also sanitised and length-capped
  before logging, so control characters cannot rewrite an operator's terminal.
- **An unknown username now costs the same as a wrong password.** Sign-in returned before hashing when
  no account matched, which made username existence measurable in a single request and defeated the
  point of answering identically for every kind of failure.
- **A monitored target can no longer inflate a check message.** Messages are capped at 1 KB where they
  are constructed, so every checker inherits it, and the DNS checker now summarises an answer set rather
  than echoing all of it on a mismatch. That text comes from the monitored host — often the very host
  whose operator would rather not be alerted — and it is stored per heartbeat, held in memory, and pasted
  into the alert body. Uncapped, a large enough answer pushed the outbound payload past what Telegram and
  Discord accept, which meant **the target could suppress the Down alert about its own outage**.
- **HTTP keyword checks read at most 256 KB of a response.** The body was previously read to completion
  with headers-only completion in force, so a target answering with `Transfer-Encoding: chunked` and
  never stopping could stream for the whole check timeout and exhaust memory. A keyword falling past the
  limit is reported as absent — the safe direction for a monitor.
- **Database monitors can require and verify TLS.** MySQL and PostgreSQL monitors gain a connection
  security setting: opportunistic (the drivers' default, and what existing monitors keep), require
  encryption, verify the certificate chain, or verify chain and hostname. The monitored database's
  password is sent on every check, and opportunistic encryption resists nothing active — an attacker on
  the network can strip it or answer in the database's place. **The default is unchanged**, so no
  existing monitor starts failing; pick a stronger mode per monitor.
- Dependencies are pinned with committed `packages.lock.json` files, so a given commit restores the same
  transitive graph for CI, a contributor and a release build alike.
- **The monitor export carries no credentials.** `/admin/export/monitors` blanks the database password,
  the HTTP auth secret, the custom header block and the push monitor's ping token. It previously blanked
  only the first, so an export — a file meant to be copied around — shipped the ping token in the clear.
  A database backup necessarily still contains everything; treat one as the credentials it carries.
- **A status page reports its own outage window**, not the correlated incident's. Because an incident
  groups monitors by shared infrastructure, a page could show "started 01:00 · ongoing" for a five-minute
  outage while the monitor row beneath it read Operational.
- **The last-administrator guard is atomic.** Two admins demoting or deleting each other at the same
  moment could both succeed and leave the instance with no administrator — a state unrepairable from the
  application. The invariant now rides in the statement itself.
- **A failed correlation lookup can no longer take a monitor's monitoring down with it.** An over-long
  hostname raised an exception the resolver did not expect, and it escaped into the heartbeat write,
  discarding the beat, the event and every notification for that monitor.
- **"Ignore TLS certificate errors" no longer re-enables redirect following.** The two per-monitor
  toggles were selecting from three clients, so ticking one silently overrode the other — an application
  answering 302 to a login page was reported healthy. *Any monitor with both "ignore TLS certificate
  errors" ticked and "follow redirects" unticked will now correctly stop following redirects, and reports
  its next check as Down.*
- **A database password that cannot be decrypted is reported as such**, instead of connecting with a
  blank one and letting the target answer "Access denied" — which sent operators after a healthy database
  when the fault was a missing key ring.
- **The shipped nginx site logs the request path without its query string** and drops the Referer, so a
  password-reset link's token is no longer written into the access log, where the `adm` group and every
  log shipper can read it.
- Endpoints are authenticated by default via an authorization fallback policy; public routes opt out
  explicitly and individually.
- Rate limiting on the anonymous push-ping endpoint (120/min per IP), on password reset (5 per 15 min
  per IP), and on sign-in (20 per 5 min per IP — uncapped, it was both an unlimited password oracle and
  a way for an anonymous caller to burn a small box's CPU in PBKDF2).
- The systemd unit sets `StateDirectoryMode=0700`, `UMask=0077` and `PrivateTmp=true`, so the data
  directory and the Data Protection key ring are unreadable by other local accounts however the install
  was performed, and the temporary file `/admin/backup` stages is not visible outside the service.
- Password reset answers identically whether or not an address has an account, so it cannot be used to
  enumerate accounts.
- **Sign-in outcomes are logged** under the `MT.Uptime.Auth` category — successes at information level,
  failures at warning, each with the submitted username and the client address (taken from `X-Real-IP`
  behind a proxy, or every line would read `127.0.0.1`). Passwords are never logged, not even their
  length. The browser still gets one deliberately vague "Invalid username or password" for every kind of
  failure; the log is where an antiforgery rejection, an unknown username and a wrong password are told
  apart. Without it, "why can I not sign in" has no answer at all — and a failed sign-in leaves no trace
  to spot a password-spray with.

### Fixed

- **Anonymous account takeover.** `/auth/profile` and `/auth/password` shipped without
  `.RequireAuthorization()`. Because the login page serves an antiforgery token to anonymous visitors, an
  attacker could mint a valid token pair, POST to `/auth/profile` to move the administrator's email
  address to one they controlled, and then use password reset to take the account over — two requests, no
  credentials. Both endpoints now require authentication, both resolve the signed-in principal rather than
  "the first user row", and an authorization fallback policy makes authenticated-by-default the rule for
  any endpoint that does not say otherwise. Covered by regression tests.
- **Webhook credentials no longer reach the system log.** For Slack, Telegram and generic webhooks the
  token is in the URL path, and `IHttpClientFactory` logs the full request URI at Information level — so
  every delivery wrote a live credential into the system log in plaintext, undoing the encryption those
  values are stored with. The notification client now uses a redacting logger that keeps host, status and
  timing but drops path and query, and logs a non-success status at Warning (a revoked webhook returns
  403 and is otherwise invisible). **Anyone who deployed before this should rotate their webhook URLs.**
- **`SQLitePCLRaw` pinned to 2.1.12** (CVE-2025-6965 / NU1903). EF Core 10.0.10 pulls 2.1.11, whose
  bundled SQLite predates 3.50.2.
- **HTTP probes send an identifying User-Agent.** A bare .NET request sends none, and many sites and WAFs
  answer a UA-less request with 403 — which the engine correctly read as down, while a browser saw the
  site working.
- **A failed probe now says why, not just that it failed.** Every checker reported `ex.Message` alone,
  and for the commonest failure an operator meets — a TLS handshake that will not complete — that
  message is a signpost rather than an answer: .NET says *"The SSL connection could not be established,
  see inner exception."* and MySQL says *"SSL Authentication Error"*. Neither contains the words
  certificate, expired, chain or name. The reason was one level down, in an exception every checker was
  discarding. Probe failures now carry the inner chain, so the alert names the actual fault — an expired
  certificate, a name mismatch, an untrusted issuer — instead of announcing that TLS is involved.

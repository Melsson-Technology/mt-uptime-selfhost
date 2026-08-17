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

### Storage

- SQLite with WAL, tuned pragmas, and incremental auto-vacuum.
- Raw heartbeats rolled into hourly and daily buckets before pruning, so long-range uptime survives on a
  small disk.
- Authenticated database backup and monitor export (secrets redacted).
- Secrets encrypted at rest via ASP.NET Core Data Protection.

### Security

- Endpoints are authenticated by default via an authorization fallback policy; public routes opt out
  explicitly and individually.
- Rate limiting on the anonymous push-ping endpoint (120/min per IP) and on password reset (5 per 15 min
  per IP).
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

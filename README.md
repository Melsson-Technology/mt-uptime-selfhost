# MT-Uptime

A self-hosted uptime monitor. Watches your sites, servers, databases and certificates, tells you when
something breaks, and shows your users a status page.

Built on .NET 10, Blazor Server and SQLite. One process, one file, no external dependencies — it runs
comfortably on a 1 GB VM.

**Licence:** [AGPL-3.0](LICENSE). Self-host it freely, modify it freely. If you offer it to others as a
hosted service, you must publish your modifications.

---

## The monitor that doesn't become the outage

Most monitoring tools poll *harder* when a target starts struggling — a failed check triggers an
immediate retry, retries stack up, and checks that haven't finished overlap with the next ones. That is
how a monitor turns a limping server into a dead one, right when you least want it.

MT-Uptime is built so it cannot do that:

- **A check never outlives its interval.** The timeout is always clamped below the interval, so two
  checks of the same monitor can never be in flight at once.
- **Failure never speeds up polling.** Retries happen *within* the existing schedule, not in addition
  to it. A target that is down gets exactly the same request rate as one that is up.
- **One request in flight per target, always.** No thundering herd when something recovers.
- **Per-type defaults that respect the answer's shelf life.** TLS certificates are checked every 6
  hours and DNS every hour, because those answers change on the order of days — not every 60 seconds.

If you are putting this on the same box as the things it watches — which is the normal self-hosting
case — this is the property that matters most, and it is the one you cannot add later by configuration.

## What it monitors

| Type | Checks |
|---|---|
| **HTTP(S)** | Status code ranges, keyword present/absent, redirects, response time. Basic/Bearer auth, custom headers, request body and a per-monitor User-Agent, for endpoints behind a login or a WAF |
| **TCP** | Port reachability |
| **DNS** | A / AAAA / CNAME / MX / TXT, optionally against a specific resolver, with expected-value matching |
| **MySQL** | Real connection and query |
| **PostgreSQL** | Real connection and query |
| **TLS certificate** | Expiry, with a configurable warning window |
| **Push / heartbeat** | The inverse: your cron job calls *us*, and we alert if the call doesn't arrive — a dead-man's switch for backups and scheduled tasks |

## What it does with that

- **Slow-response alerting.** An HTTP 200 that takes eight seconds is still a problem. Set a threshold
  and the monitor reports **Slow** — but only after N consecutive slow checks, because response time is
  spiky and one bad sample is noise.
- **Retry windows.** A single failed check doesn't page you. Configure how many consecutive failures
  confirm an outage. A definitive negative (a bad HTTP status) skips the wait, because retrying won't
  change the answer.
- **Tags and dashboard filtering.** Label monitors by environment, customer or host and filter to them
  in one click. The list stops being readable somewhere past thirty monitors without this.
- **Public status pages** at `/status/{slug}`, with 30-day uptime per monitor.
- **Notifications** to email (SendGrid), Slack, Discord, Microsoft Teams, Telegram, ntfy, Gotify,
  PagerDuty or a generic webhook — globally or per monitor. PagerDuty gets real incident semantics: a
  recovery *resolves* the incident instead of paging someone about a service that's already back.
- **History that survives pruning.** Raw heartbeats are rolled into hourly and daily buckets before
  they're deleted, so long-range uptime percentages stay accurate on a small disk.
- **Live dashboard.** Status updates push to the browser over the existing Blazor circuit — no polling,
  no JavaScript framework.
- **Users and roles.** Admin, Editor and Viewer, so you can bring in colleagues without handing over the
  keys to the instance.

## Quick start

### Docker

```bash
git clone https://git.melssontechnology.com/Melsson-Technology/mt-uptime-selfhost.git
cd mt-uptime-selfhost/docker && docker compose up -d
```

Open <http://localhost:5081/> and complete the setup wizard.

**No prebuilt image is published yet**, so Compose builds one from this repository the first time —
a few minutes, and Docker is the only thing you need installed. The `Dockerfile` targets both
`linux/amd64` and `linux/arm64`, so a Raspberry Pi or an ARM VPS builds and runs the same as an x86
box. When published images exist, this becomes a pull and the change is one line in
`docker-compose.yml`.

One thing worth knowing before you change the volume: `/var/lib/mt-uptime` holds the database **and**
the keys that decrypt every secret stored in it. Mount only the database and MT-Uptime starts, reports
healthy, and cannot read a single stored credential — silently. [docker/README.md](docker/README.md)
covers this, bind-mount permissions, and backups.

### From source

Requires a **.NET 10 SDK** (10.0.3xx — pinned in `global.json`).

```bash
git clone https://git.melssontechnology.com/Melsson-Technology/mt-uptime-selfhost.git
cd mt-uptime-selfhost
./scripts/run.sh          # or .\scripts\run.ps1 on Windows
```

Then open <http://localhost:5081> and complete the setup wizard. That's it — the database is created on
first run, and there's nothing else to configure.

To run the tests:

```bash
./scripts/test.sh         # or .\scripts\test.ps1
```

## Deploying

See **[deploy/README-deploy.md](deploy/README-deploy.md)** for a full walkthrough: systemd unit, nginx
reverse proxy, Let's Encrypt, and the provisioning script.

```bash
./scripts/build-and-package.sh    # produces build/mt-uptime.tar.gz
# copy to the server, then:
sudo ./deploy/deploy-on-server.sh mt-uptime.tar.gz
```

> **Back up `/var/lib/mt-uptime/` — all of it.** It holds the SQLite database *and* the Data Protection
> keys. Losing the keys makes every stored secret (SendGrid key, database passwords, webhook URLs)
> permanently undecryptable, even with a perfect copy of the database.

## Configuration

Almost nothing is configured by file — email, notification channels, retention and monitors are all set
in the UI and stored encrypted in the database. The exceptions:

| Setting | Default | Purpose |
|---|---|---|
| `Storage:DatabasePath` | `App_Data/mt-uptime.db` | SQLite file location |
| `Storage:DataProtectionKeysPath` | `App_Data/keys` | Where secrets-at-rest keys live |
| `Engine:MaxConcurrentChecks` | auto (8–32) | Cap on simultaneous checks |
| `Engine:RawRetentionDays` | 30 | Before raw heartbeats are rolled up and pruned |

Both `Storage:*` values are environment-bindable (`Storage__DatabasePath`), which is how the systemd unit
points them at `/var/lib/mt-uptime`.

## A note on what this is not

MT-Uptime has three roles — Admin, Editor, Viewer — so a team can share an instance. That is a boundary
between **colleagues**, not a sandbox for untrusted users: an Editor configures monitors, which means
causing outbound connections to hosts of their choosing and seeing what the instance watches. Anyone with
Admin can download the whole database. See [SECURITY.md](SECURITY.md) for the full threat model —
including why the checkers can connect to any host and port, and why that is deliberate.

## What's free and what isn't

Everything in this repository is AGPL-3.0 and stays that way. There is no crippled edition: **the hosted
version at melssontechnology.com runs this exact code.** Monitor types, notification channels, users and
roles, the API, status pages — all of it is here, and none of it is withheld to sell you an upgrade.

Two things are not in this repository, and we would rather you learn that here than after adopting it:

- **The hosted service.** Probe locations around the world, someone watching the watcher, managed
  backups, SMS credits, upgrades you don't perform. None of that can ship as a tarball — it's
  operations, not features.
- **Multi-client white-labelling.** Per-client branded status pages and SLA reports, aimed at agencies
  and MSPs billing for monitoring. This *is* an engine feature we hold back, and it's the one that
  funds the rest. If you're monitoring your own infrastructure, you will never hit it.

We would rather say this plainly than have you discover it later. If it changes, it will change in this
file first.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The test suite is hermetic — no external services, no environment
variables — so `./scripts/test.sh` works on a fresh clone with nothing but the SDK installed.

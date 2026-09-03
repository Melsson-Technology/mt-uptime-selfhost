# End-to-end battery

Everything in `engine/Tests.MT-Uptime` is hermetic on purpose: throwaway SQLite files, no external
services, no environment variables. That is a promise worth keeping, and it leaves a gap. Nothing in it
has ever proved, on a clean machine, that

* the install described in [`deploy/README-deploy.md`](../deploy/README-deploy.md) actually works, or
* any of the seven monitor types detects a real service going **Up → Down → Up**.

Tcp, Dns and Tls have no behavioural tests at all; `HttpCheckerTests` drives a stubbed message handler;
the only real socket in the whole hermetic suite is a deliberately-failing connect to `127.0.0.1:1`.

This directory closes that gap. It prepares a disposable machine with a real service behind every
monitor type — an HTTP fixture behind nginx on plain HTTP and on four HTTPS ports with four different
certificates, a TCP listener, a closed port, a blackholed port, an authoritative DNS zone, MySQL and
PostgreSQL with TLS from a locally-minted CA — and gives the tests a root-owned helper that can break
and restore each one on demand.

> **Status: complete, and never yet run on a real box.** All four tiers are written; nothing in the
> checker, pipeline or UI tiers has executed against actual target services. Treat the first run as
> the test of the battery as much as of the product. See "What is built" at the bottom.

## What you need

A **disposable** Ubuntu 24.04 machine you are willing to throw away — a `t3.medium` with 30 GB is
comfortable. Not a machine that runs anything you care about: this installs and reconfigures MySQL,
PostgreSQL, nginx and dnsmasq, adds a CA to the system trust store, and installs an nftables rule.

`install-targets.sh` refuses to run on a host without `apt-get`, because every package name, the
AppArmor profile path, the PostgreSQL cluster layout and the systemd unit names below are
Debian-family specifics.

## Running it

```bash
# 1. Targets. Idempotent — the acceptance bar is that its self-check passes twice in a row.
sudo ./e2e/install-targets.sh --with-ui

# 2. Install MT-Uptime by hand, following deploy/README-deploy.md's "short version".
#    By hand is deliberate: every README command that misbehaves is a finding.
#    Skip certbot, and leave App__PublicBaseUrl unset.

# 3. Tier 0 — completes first-run setup and smoke-tests the install.
./e2e/smoke.sh

# 4. The tiers.
./e2e/run-tests.sh --tier checker
./e2e/run-tests.sh --tier pipeline
./e2e/run-tests.sh --tier ui
```

`install-targets.sh` can run before or after the application is installed. It writes its nginx
configuration to `/etc/nginx/conf.d/` rather than `sites-enabled/` specifically so that the ordering
does not matter — see the comment at the top of `targets/nginx-e2e.conf` for why a file in
`sites-enabled` would change what `provision.sh` decides about Ubuntu's default site, and make the
product's own `/healthz` return 404.

Useful flags: `--only <step>` runs one step (`certs`, `fixture`, `nginx`, `tcp`, `blackhole`, `dns`,
`mysql`, `postgres`, `helper`, `ui`, `manifest`); `--with-ui` adds Chromium's shared libraries for the
Playwright tier; `--no-selfcheck` skips the PASS/FAIL table, which you should never do for a real run,
because that table is the only thing separating "the script finished" from "the box is ready".

## The four tiers

| Tier | What it proves | Driver |
|---|---|---|
| **0 — Smoke** | The documented install works: health, first-run token, login, anonymous boundaries, push ping, rate limits, admin export/backup | `smoke.sh` (bash + curl + sqlite3) |
| **1 — Checkers** | Every checker against a real service, asserting the exact status, hard/soft flag and message | xUnit, checkers resolved from the real container |
| **2 — Pipeline** | scheduler → checker → state machine → heartbeats/incidents → webhook, including retries, Degraded, Timeout and overdue Push | xUnit + `WebApplicationFactory` + break/restore |
| **3 — UI** | The installed instance driven through every Blazor form, including a live dashboard flip with no reload | xUnit + Playwright (headless Chromium) |

## The manifest

`install-targets.sh` writes `/etc/mt-uptime-e2e/targets.env` — every port, credential, DNS record and
certificate expiry the tests need. It is rewritten in full on each run, as unquoted `KEY=VALUE`, so
that the shell (`source`) and the test suite (`Support/Targets.cs`) read it identically; the
installer's self-check round-trips it through both shapes so the two cannot drift apart.

It is `0640 root:<test user>`, because it holds the database passwords.

**Without a readable manifest every test reports `SKIPPED`, not failed.** So
`dotnet test engine/Tests.E2E.MT-Uptime` is safe to run anywhere — a laptop that has never seen an E2E
box included. Point it elsewhere with `MTU_E2E_MANIFEST=/path/to/targets.env`.

## Breaking things by hand

```bash
sudo mt-uptime-e2e-target status              # every target's current state
sudo mt-uptime-e2e-target break   http        # /toggle answers 503
sudo mt-uptime-e2e-target restore http
sudo mt-uptime-e2e-target break   http-slow   # /toggle sleeps 1500 ms
sudo mt-uptime-e2e-target break   tcp         # stops the listener; the port refuses
```

Targets: `http`, `http-slow`, `tcp`, `dns`, `mysql`, `postgres`, `all`.

Every verb **blocks until the change is observable from outside** — the port really refuses, the flag
file really produces a 503 — or fails after 60 seconds. That is so the tests never have to poll for the
break itself: `systemctl stop mysql` returns before the port has finished closing, and a test that
began asserting Down immediately would occasionally catch one last healthy check.

`all` deliberately omits `http-slow`: the fixture checks the down flag before the slow flag, so once
`http` is broken the slow flag has no observable effect. The two HTTP breaks are mutually exclusive by
construction.

The test user reaches the helper through a `NOPASSWD` sudoers rule that enumerates exactly those
verb/target pairs — no wildcards, no `systemctl`, no `nft`. The helper is `0755 root:root`, which is
load-bearing rather than tidy: a `NOPASSWD` rule pointing at a file its grantee can write is a root
shell for the asking.

## Why this is not in the solution

`Tests.E2E.MT-Uptime` is **not** a member of `MT-Uptime.Engine.slnx`, so `./scripts/test.sh` never sees
it and continues to report exactly 360 hermetic tests. Run this suite with `./e2e/run-tests.sh`, or by
path with `dotnet test engine/Tests.E2E.MT-Uptime`.

The whole assembly also runs its tests **one at a time**
(`[assembly: CollectionBehavior(DisableTestParallelization = true)]`), for two reasons. The target
services are shared and singular, so one class calling `restore http` while another asserts Down is not
a race that can be tuned away. And incidents correlate by host: on this box every host is `127.0.0.1`,
so every HTTP, TCP and database monitor shares one correlation key and concurrent failures would merge
into a single incident that neither test set up.

## No certificates are committed

`scripts/publish-public.sh` refuses to publish if a `.crt`, `.key` or `.pem` is tracked anywhere under
`engine/`, which is why `targets/make-certs.sh` mints everything at runtime into
`/etc/mt-uptime-e2e/certs`. Keep it that way: a test certificate in a public repository is still a
private key in a public repository.

The certificate set is regenerated whenever a leaf drifts out of the window its tests describe — the
"expiring in 5 days" certificate stops meaning that after a week — and it is built in a staging
directory and swapped in with a rename, so an interrupted run cannot leave the box with no
certificates at all.

## What is built

| | |
|---|---|
| ✅ `install-targets.sh` + `targets/` | The whole target layer, with a 47-assertion self-check |
| ✅ `Tests.E2E.MT-Uptime` harness | `Targets`, `E2EFact`/`E2ETheory`/`UIFact`, `CheckerHost`, `E2EAppFactory`, and 7 harness tests |
| ✅ `smoke.sh` (Tier 0) | 30-odd checks; completes first-run setup and records the administrator |
| ✅ `run-tests.sh` | Tier selection, the manifest gate, the Chromium install, the empty-tier guard |
| ✅ `install-mt-uptime.sh` | A replay of the deploy README, for the second install onward |
| ✅ `Support/TargetControl.cs`, `Support/WebhookSink.cs` | Break/restore with restore-on-dispose; an HTTP endpoint alerts are delivered to |
| ✅ Tier 1 — the checker matrix | **113 tests** across the six actively-probed monitor types |
| ✅ Tier 2 — pipeline scenarios | **21 scenarios** driving the whole running engine, target to webhook |
| ✅ Tier 3 — the browser tier | **18 tests** driving the installed instance through headless Chromium |

The harness tests are the ones worth knowing about, because they answer the questions everything else
rests on: that a second test assembly in this repository can boot
`WebApplicationFactory<Program>` and get a 200 from `/healthz`; that all six actively-probed checkers
resolve from the real container; and that the skip mechanism genuinely gates on the manifest — 6 passed
and 1 skipped without one, 7 passed with one.

Every HTTP assertion `smoke.sh` makes was verified against a **published Release build** of the real
application before it ever reached a box: 30 checks passing, plus six negative controls proving each
predicate can still fail. Two things that verification changed, both of which would otherwise have
been discovered the slow way:

* **An unknown status-page slug answers `200`, not `404`.** `PublicStatus.razor` sets no status code;
  it renders "This status page is not available." with a success code. The check asserts what the
  product does and the discrepancy is recorded as a finding — a 200 for a page that does not exist is
  wrong for anything that crawls or monitors it.
* **`/_framework/blazor.web.js` is only a `200` on a published build.** Run from source with
  `dotnet run`, a Debug build answers `500`: `MapStaticAssets` attaches the framework's development
  runtime handler, which looks for the file under `wwwroot/_framework` where it has never been
  written. Nothing to fix — the installed instance is always a publish — but it is an hour lost to
  anyone who tries to reproduce that one check locally.

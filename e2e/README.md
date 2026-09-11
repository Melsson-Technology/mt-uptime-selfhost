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

> **Status: complete, and proven on a real box.** All four tiers have run against actual target
> services — **Tier 1 at its full 122 on 2026-09-10**, alongside 50/50 targets (twice), 36/36 Tier 0,
> 21/21 Tier 2 and 18/18 Tier 3. Two of the Tier 1 tests assert a documented product limitation rather
> than expecting it to work (MySQL `VerifyFull`, see `MySqlCheckerE2E`), so their passing is the
> intended outcome.
>
> `HttpDiagnosticsE2E` — eight tests covering the evidence a failing HTTP check keeps — was added after
> an earlier run showed the battery had no coverage of that feature at all, and it is what took Tier 1
> from 114 to 122. All eight have since run green on a prepared box, including the two that need the
> battery's own targets: `break http`, and the expired certificate on `HTTPS_EXPIRED_PORT`.
>
> Getting there took eighteen fixes to the battery itself, and it is worth saying what kind: systemd
> cutting a command at a semicolon, a umask leaking into a directory two hundred lines from where it
> was set, a glob expanding in the wrong shell, a Blazor circuit that had not connected yet. None of
> them were reachable by reading the code. That is the argument for this directory existing.

## What you need

A **disposable** Ubuntu 24.04 machine you are willing to throw away — a `t3.medium` with 30 GB is
comfortable. Not a machine that runs anything you care about: this installs and reconfigures MySQL,
PostgreSQL, nginx and dnsmasq, adds a CA to the system trust store, and installs an nftables rule.

`install-targets.sh` refuses to run on a host without `apt-get`, because every package name, the
AppArmor profile path, the PostgreSQL cluster layout and the systemd unit names below are
Debian-family specifics.

## Running it

**There are two ways to run this, and which one you want depends on why.**

If you downloaded the repository and want to watch the monitors work against real services, take the
scripted path below: five commands, and nothing to decide.

If you are **validating a release**, do step 3 by hand instead, following
`deploy/README-deploy.md`'s "short version" literally. That is the test — the README is the product's
install instructions, and every command in it that misbehaves is a finding. `install-mt-uptime.sh`
replays those same commands in the same order with no fixes applied, so it reproduces such defects
rather than revealing them.

### 0. A machine you are going to destroy

Ubuntu 24.04, `t3.medium`, 30 GB. See **What you need** above, and mean it about disposable.

On a box that will live more than a few hours, stop unattended upgrades restarting MySQL or
PostgreSQL in the middle of a run. Nothing in the battery does this for you:

```bash
sudo systemctl disable --now apt-daily.timer apt-daily-upgrade.timer
```

### 1. Clone

```bash
sudo apt-get update && sudo apt-get install -y git
git clone https://github.com/Melsson-Technology/mt-uptime-selfhost.git
cd mt-uptime-selfhost
```

### 2. The targets

```bash
sudo ./e2e/install-targets.sh --with-ui
```

Three to six minutes, ending in a PASS/FAIL table. **Expect `50 / 50`.** Run it a second time if you
want the bar the maintainers hold it to: the table passing twice in a row is what separates a script
that converges from one that merely finished.

Use `sudo` from your own account rather than from a root shell. `E2E_TEST_USER` defaults to
`$SUDO_USER`, and that account is the one the manifest and the sudoers rule are written for.

### 3. MT-Uptime itself

```bash
sudo ./e2e/install-mt-uptime.sh $(hostname -f)
export PATH=$HOME/.dotnet:$PATH
```

Installs the .NET SDK if you have none, builds a Release package here, provisions the host and
deploys it. Certbot is deliberately skipped — the battery is plain HTTP on `:80` — and
`App__PublicBaseUrl` deliberately left unset.

**The `export` is not decoration.** The SDK is installed into *your* home directory rather than
root's, specifically so the account running the tests can reach it, which means your own shell has to
be told where it went. Every step after this one needs `dotnet` on `PATH`.

### 4. Tier 0 — smoke

```bash
./e2e/smoke.sh
```

**Expect `36 / 36` and one warn.** This is the one step with no second chance: it completes the
first-run wizard, and the setup token is destroyed the moment an administrator exists. So this is the
only opportunity to capture those credentials, and it writes them into the manifest — without them
the UI tier skips itself entirely. **Do not complete the wizard in a browser first.**

### 5. The three test tiers

```bash
./e2e/run-tests.sh --tier checker
./e2e/run-tests.sh --tier pipeline
./e2e/run-tests.sh --tier ui
```

**Expect `122`, `21` and `18` passing.** Two of the checker tests assert the MySQL `VerifyFull`
limitation rather than expecting it to work, so those passing is the correct outcome, not a mystery.

**Keep them in that order.** `smoke.sh` deliberately exhausts the sign-in limiter — 20 attempts per
five minutes, keyed on the connection address, which behind nginx is `127.0.0.1` for everything on
this box — so the UI tier cannot sign in for up to five minutes afterwards. The checker and pipeline
tiers need no login and absorb that wait for free, and `smoke.sh` prints when the limiter is clear.
`--tier all` exists, but xUnit chooses the order inside it, so the cooldown can land *on* the UI
tests rather than ahead of them.

> **The tier used to spend the entire sign-in budget, and no longer does.** Measured on 2026-09-10:
> eighteen tests performed **twenty** successful sign-ins, against a limit of exactly **20 per five
> minutes partitioned by client address** — and that address is the same for every test, because nginx
> forwards it and `UseForwardedHeaders` resolves it back to this box's loopback. The tier was passing
> on whether the fixed window happened to roll mid-run, and a nineteenth test tipped it over: the run
> failed *inside* `UiFixture.SignInAsync`, with a navigation timeout that reads like a broken page
> rather than a spent budget.
>
> The limit is not the thing to change — it is what makes offline-speed password guessing impractical
> and stops an anonymous caller starving the monitoring runners of the PBKDF2 CPU they share. So
> `UiFixture` now signs the administrator in **once per test class** and seeds every later context from
> the saved cookie jar (`StorageStateAsync`). Each test still gets its own isolated context. **Sign-ins
> per run went 20 → 5**, and a nineteenth test costs one more rather than one per test.
>
> The seeded session is verified rather than assumed: if the replayed jar no longer authenticates the
> fixture falls back to a real sign-in. That check is the difference between this working and the first
> attempt at it, which cached the state and trusted it — an unauthenticated context does not error, it
> quietly redirects to `/login`, and every later locator then times out somewhere unrelated.
>
> **Known flakiness, and it is not the budget.** `U5` (incident acknowledged and annotated) and to a
> lesser extent `U11` (monitor edited and deleted) fail intermittently on a loaded box — in their own
> test bodies, with zero sign-in rejections in the log. Observed across five runs on 2026-09-10: 18/18,
> 18/18, 19/19, 18/19, 16/18. If you get a failure here, check *where* it failed before assuming the
> tier is broken, and re-run.

### 6. Destroy the machine

Terminate it. Not stop, and do not take an image: it holds the target databases' passwords, a private
CA in its system trust store, a `NOPASSWD` sudoers rule and an nftables rule.

### When a script refuses

Every refusal here is deliberate, and each one prints its own fix. These are the ones worth
recognising on sight:

| What you see | What it means |
|---|---|
| `REFUSING: … apt-get is not present` | Not a Debian-family host. The package names, the AppArmor profile path and the PostgreSQL cluster layout are all Debian specifics |
| `REFUSING: no target manifest at …` | Step 2 has not run. Running the tests without it would report success having tested nothing |
| `REFUSING: … exists but this user cannot read it` | The manifest is `0640 root:<test user>`. Re-run step 2 with `sudo` from the account you intend to test as, or set `E2E_TEST_USER` |
| `REFUSING: dotnet is not on PATH` | The `export` from step 3, in this shell |
| `REFUSING: the manifest has no MTU_BASE_URL/MTU_ADMIN_PASSWORD` | Step 4 has not run, so every UI test would skip |
| `REFUSING: nothing to run — no test matched the tier` | A mistyped `--filter`, or a renamed namespace. `dotnet test` exits **zero** when its filter matches nothing, so an empty tier is otherwise indistinguishable from a green one |
| Every test `SKIPPED`, none failed | No readable manifest. That is by design, so the suite is harmless on a laptop — see **The manifest** below |

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
it and continues to report exactly 371 hermetic tests. Run this suite with `./e2e/run-tests.sh`, or by
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
| ✅ `install-targets.sh` + `targets/` | The whole target layer, with a 49-assertion self-check |
| ✅ `Tests.E2E.MT-Uptime` harness | `Targets`, `E2EFact`/`E2ETheory`/`UIFact`, `CheckerHost`, `E2EAppFactory`, and 7 harness tests |
| ✅ `smoke.sh` (Tier 0) | 30-odd checks; completes first-run setup and records the administrator |
| ✅ `run-tests.sh` | Tier selection, the manifest gate, the Chromium install, the empty-tier guard |
| ✅ `install-mt-uptime.sh` | A replay of the deploy README, for the second install onward |
| ✅ `Support/TargetControl.cs`, `Support/WebhookSink.cs` | Break/restore with restore-on-dispose; an HTTP endpoint alerts are delivered to |
| ✅ Tier 1 — the checker matrix | **122 tests** across the six actively-probed monitor types, including the failure-diagnostics capture |
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

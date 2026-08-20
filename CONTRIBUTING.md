# Contributing to MT-Uptime

## Code contributions are not open yet

**Issues are very welcome** — bug reports, questions, feature requests, "this documentation is wrong",
"this failed on my distro". Those are the most useful thing you can send right now, and they need no
paperwork.

**Pull requests are not being accepted yet.** Please open an issue instead; if it's something you'd like
to fix yourself, say so there and we'll get back to you when this changes.

The reason is licensing, and it is easier to be straight about it than to leave PRs sitting unanswered.
MT-Uptime is AGPL-3.0, and Melsson Technology also intends to offer a hosted version. That combination
works only while Melsson holds the rights to relicense the codebase, which means an outside contribution
needs a contributor agreement granting those rights **before** it is merged. There is no way to add one
retroactively — it would mean tracking down every past contributor and getting each to agree, and any who
declined, or who could not be reached, would be permanent.

So rather than merge contributions now and create that problem, contributions are closed until the
agreement exists. It is on the roadmap, not a policy of principle.

None of this restricts what the AGPL already grants you. Fork it, modify it, run your own version, ship it
to others under the same licence — that is what the licence is for, and it needs no permission from us.
The rest of this document is written to make that easier as much as it is written for future contributors.

## Getting started

You need a **.NET 10 SDK**, specifically a 10.0.3xx build — `global.json` pins it, so the build will tell
you if yours is wrong. Nothing else: no database to install, no services to run, no environment variables.

```bash
./scripts/test.sh     # 360 tests, under a minute
./scripts/run.sh      # http://localhost:5081
```

On Windows use the `.ps1` equivalents. The tests are hermetic — they create throwaway SQLite files in the
temp directory and clean up after themselves — so a fresh clone works immediately.

## Layout

```
Core.MT-Uptime/       the engine: checkers, state machine, scheduler, notifications, retention
SelfHost.MT-Uptime/   the Blazor Server host: pages, endpoints, auth
Tests.MT-Uptime/      xUnit
deploy/               systemd unit, nginx sample, deployment guide
scripts/              build / test / run, as .ps1 + .sh pairs
```

Project folders are named `Role.MT-Uptime`, but the assemblies and namespaces are `MT.Uptime.*` — the
`.csproj` files set `AssemblyName` and `RootNamespace` explicitly. If you add a project, follow that
pattern, and note that the published assembly name is load-bearing: `deploy/mt-uptime.service` executes
`MT.Uptime.Web.dll` by name.

## How the engine fits together

Worth understanding before changing anything under `Core.MT-Uptime/Monitoring`:

- **One runner per monitor.** `MonitorSchedulerService` starts a `MonitorRunner` per enabled monitor, each
  a `PeriodicTimer` loop. Idle they cost almost nothing. A shared semaphore caps concurrent checks.
- **The state machine is pure.** `MonitorStateMachine.Evaluate(status, hard, slow)` does no I/O and reads
  no clock, which is why it can be exhaustively unit-tested. Keep it that way — if you need a timestamp,
  pass it in.
- **One writer.** Runners push results into a channel; `HeartbeatWriter` drains it one at a time. SQLite
  has a single write lock, and this is how concurrent checks avoid fighting over it. Don't write to the
  database from a checker.
- **Checkers answer Up or Down only.** Retry windows, degraded detection and state transitions are the
  engine's job, not the checker's.

## Adding a monitor type

1. Add a value to `MonitorType` in `Core.MT-Uptime/Domain/Enums.cs`.
2. Implement `IMonitorChecker` and register it in `ServiceCollectionExtensions`.
3. Add a config record under `Monitoring/Configs/` — per-type settings live in `Monitor.ConfigJson`, so
   **no database migration is needed**.
4. Add the form fields to `MonitorEdit.razor`.
5. Add tests. `HttpCheckerTests` shows the pattern for a checker with a stubbed `IHttpClientFactory`.

## Things that will get a change sent back

- **Polling harder when a target is failing.** The interval is fixed in every state, deliberately. Many
  monitoring tools shorten it during retries, which is how a monitor turns a struggling server into a dead
  one. `MonitorCadence` holds the guardrails; it also enforces that a check's timeout is always shorter
  than its interval, so checks can never run back-to-back.
- **Adding a background service without failure containment.** Every loop catches per iteration. An
  unhandled exception in a `BackgroundService` takes the whole process down by default.
- **Changing the Data Protection purpose string** in `DataProtectionSecretProtector` — that makes every
  existing installation's stored secrets undecryptable.
- **Skipping tests for engine logic.** UI polish doesn't need a test; a state transition does.

## Tests

```
StateMachineTests / DegradedStateTests    the pure decision logic, exhaustively
MonitorCadenceTests                       interval/timeout guardrails
HttpCheckerTests                          a checker against a stubbed HTTP client
RetentionTests                            rollup and pruning against a real temp SQLite database
UserAccountTests                          accounts and the password-reset token lifecycle
EndpointAuthorizationTests                which endpoints an anonymous caller may reach
```

`EndpointAuthorizationTests` boots the real pipeline through `WebApplicationFactory`, because endpoint
authorization only exists in the middleware chain — you cannot unit-test it from a handler.

## Style

`.editorconfig` covers the mechanics. Beyond that: comments should explain **why**, not what. The
codebase leans heavily on this, especially where something looks odd but is deliberate — the SQLite
connection-pooling workaround in the backup endpoint, the reason auth pages are static SSR, the reason the
retry window exists. If you work out something subtle, leave the explanation behind.

## Reporting security issues

Please don't open a public issue. See [SECURITY.md](SECURITY.md).

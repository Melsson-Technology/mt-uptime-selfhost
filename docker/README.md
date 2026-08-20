# Running MT-Uptime in Docker

```bash
docker compose up -d

# The setup wizard needs a one-time token, printed on first boot:
docker compose logs | grep -A2 "First-run setup is open"
```

On Windows, `grep` is not present and PowerShell does not accept `&&` as a statement separator — so
run each line on its own and use `Select-String` for the last one:

```powershell
docker compose logs | Select-String -Context 0,2 "First-run setup is open"
```

Then open <http://localhost:5081/> and complete the setup wizard, pasting that token. The database is
created on first boot; there is nothing to install and no external services to configure.

The token exists because the wizard mints an administrator and cannot require a login — it runs before
any account exists. Without it, anyone who can reach the port during the window between first boot and
you finishing the form becomes the administrator, which matters the moment this is published anywhere
less private than your laptop. It is written to `/var/lib/mt-uptime/setup-token` inside the volume, and
destroyed once the account is created.

## Exposing it beyond loopback

`docker-compose.yml` publishes on `127.0.0.1:5081` — reachable from the Docker host and nowhere else.
That is the safe default, not an oversight: Kestrel serves plain HTTP, so anything wider puts an
unencrypted login form on the network.

To reach it from elsewhere, put a reverse proxy in front and terminate TLS there
(`deploy/mt-uptime.nginx.conf.sample` is a worked example). Two settings matter when you do:

```yaml
environment:
  # The public address. Password-reset links are built from this rather than from the request's Host
  # header, which the caller controls. Setting it also narrows AllowedHosts to this hostname.
  App__PublicBaseUrl: "https://uptime.example.com"

  # Only needed when the proxy is NOT on loopback — nginx in another container, or a load balancer.
  # X-Forwarded-For is trusted from loopback by default and from nothing else, because a forwarded
  # header from an untrusted source is just a client-chosen value: both rate limiters partition on the
  # resulting address, so trusting it blindly means an attacker rotating the header gets an unlimited
  # number of buckets and no cap applies. Declare the proxy's address or network instead.
  ForwardedHeaders__KnownProxies: "172.18.0.0/16"
```

A malformed value here stops the app at startup rather than silently trusting nothing — being quietly
wrong about this looks like working software right up until the limiter matters.

> **Note on rate limiting under Docker.** With the default bridge network, connections are NATed and the
> container often sees the gateway address rather than the real client, so per-IP limits behave as one
> shared bucket. A reverse proxy that sets `X-Forwarded-For`, declared via `ForwardedHeaders__KnownProxies`
> above, is what restores per-client limits.

## There is no prebuilt image yet — Compose builds one

**Nothing is published to a registry at the moment**, so `docker-compose.yml` builds the image from
the source in this repository. The `Dockerfile` is multi-stage, so you need Docker and nothing else —
no .NET SDK on your machine. The first run compiles the application and takes a few minutes; after
that Docker caches it and startup is immediate.

That means you need the repository, not just the Compose file:

```bash
git clone https://git.melssontechnology.com/Melsson-Technology/mt-uptime-selfhost.git
cd mt-uptime-selfhost/docker
docker compose up -d
```

When images are published this will become a pull, and the change will be one line in
`docker-compose.yml`. Until then, treat any `mt-uptime:latest` reference you find as aspirational.

To run it without Compose, build the image yourself first — note the build context is the repository
root, not this directory:

```bash
docker build -f docker/Dockerfile -t mt-uptime:local .

docker volume create mt-uptime-data
docker run -d --name mt-uptime --restart unless-stopped \
  -p 5081:8080 \
  -v mt-uptime-data:/var/lib/mt-uptime \
  mt-uptime:local
```

## If you change the Dockerfile, check interactivity — not just that it starts

The build deliberately does **not** pass `--no-restore` to `dotnet publish`, even though a restore layer
sits right above it. Framework static web assets — chiefly `_framework/blazor.web.js` — are resolved
during restore, and skipping it leaves them out of `wwwroot` and out of the endpoints manifest.

Nothing about that failure looks like a build problem. The container builds, starts, passes its health
check and serves the setup wizard, because those are static pages and plain form posts. But
`blazor.web.js` 404s, interactivity never starts, every interactive page silently degrades to an ordinary
HTML form, and the first thing anyone does — adding a monitor — posts to a route with no handler and
returns **400 Bad Request**.

So after any change to the build, verify one interactive action, not just `/healthz`:

```bash
curl -sI http://localhost:5081/_framework/blazor.web.js   # must be 200, not 404
```

## Read this before you choose where the data goes

`/var/lib/mt-uptime` holds two things, and **they cannot be separated**:

- `mt-uptime.db` — the SQLite database.
- `keys/` — the Data Protection keys that decrypt every secret *in* that database: SendGrid
  credentials, monitored-database passwords, webhook URLs, Telegram bot tokens.

Mount a volume covering only the database and MT-Uptime will start, migrate, and report healthy —
while unable to decrypt a single stored secret. Nothing throws an error. Your notification channels
just quietly stop working, and the reason is not visible anywhere in the UI.

The same applies to backups. A copy of the database without the keys is not a backup; restore it and
every secret in it is permanently unreadable. **Back up the whole directory, together.**

So: one volume, covering `/var/lib/mt-uptime`. Don't narrow it.

## Bind mounts need a chown first

A named volume inherits ownership from the image, so it works with no preparation. A bind mount does
not — Docker keeps the host directory's ownership — and the container runs as UID **1654**, so
without this it cannot write and exits immediately on start:

```bash
mkdir -p data && sudo chown -R 1654:1654 data
```

Then swap the volume line in `docker-compose.yml` for `- ./data:/var/lib/mt-uptime`.

Named volumes are the better default. Use a bind mount when you want the files somewhere specific for
your own backup tooling to reach.

## Ports and TLS

The container listens on **8080**, and Compose maps it to **5081** on the host to match the
non-Docker default. Change the left-hand number only; the right-hand one is fixed by
`ASPNETCORE_URLS` in the image.

Kestrel serves plain HTTP and expects a reverse proxy to terminate TLS. If you put one in front, it
**must forward the WebSocket upgrade** — the dashboard is a Blazor Server circuit, so without it the
page loads normally and then silently never updates, which looks like a broken app rather than a
broken proxy. [`deploy/mt-uptime.nginx.conf.sample`](../deploy/mt-uptime.nginx.conf.sample) is a
worked example; the `proxy_pass` port there is the host port you mapped.

## Backup and restore

```bash
# Back up the whole state directory, database and keys together.
docker run --rm -v mt-uptime-data:/data -v "$PWD:/out" alpine \
  tar czf /out/mt-uptime-backup.tar.gz -C /data .

# Restore into a fresh volume.
docker run --rm -v mt-uptime-data:/data -v "$PWD:/in" alpine \
  tar xzf /in/mt-uptime-backup.tar.gz -C /data
```

Stop the container first for a clean copy, or use the authenticated `/admin/backup` endpoint, which
checkpoints SQLite properly while running.

**Perform a restore once, before you need one.** A backup nobody has restored is a hypothesis.

## Upgrading

```bash
docker compose pull
docker compose up -d
```

Pending EF migrations apply automatically at startup. The volume is untouched. `latest` moves with
every release, so pin the twelve-character SHA tag if you would rather choose when to move.

## Building it yourself

The build context is the **repository root**, not this directory:

```bash
docker build -f docker/Dockerfile -t mt-uptime .
```

Multi-architecture, as CI builds it:

```bash
docker buildx build -f docker/Dockerfile \
  --platform linux/amd64,linux/arm64 -t mt-uptime .
```

The SDK stage pins itself to the builder's architecture and cross-publishes with
`dotnet publish -a $TARGETARCH`, so building arm64 does not mean emulating the entire .NET SDK under
QEMU — which works, but is slow enough that people stop doing it. Only the runtime stage's `apt-get`
is emulated.

## Automated builds

[`.gitea/workflows/docker-image.yml`](../.gitea/workflows/docker-image.yml) builds and pushes
`linux/amd64` and `linux/arm64` on every push to `main`, tagging `latest` and the short commit SHA.

It needs a registered runner with access to a Docker daemon:

```bash
# On the runner host, against your Gitea instance:
act_runner register --instance https://git.example.com --token <from Settings -> Actions -> Runners>
```

Give it a label matching `runs-on` in the workflow (`ubuntu-latest` by default) and either mount
`/var/run/docker.sock` or run it with dind. Until a runner exists the workflow simply never fires;
nothing else is affected, and the image can still be built by hand as above.

Pushing to the Gitea registry needs no configuration — `GITEA_TOKEN` is provided automatically.
Docker Hub is optional: set the `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets and the
extra push turns itself on, with no edit to the workflow.

## Environment variables

Everything below is optional; the image sets sensible defaults.

| Variable | Default | Notes |
|---|---|---|
| `Storage__DatabasePath` | `/var/lib/mt-uptime/mt-uptime.db` | Change both storage paths together, and keep them in one mounted directory |
| `Storage__DataProtectionKeysPath` | `/var/lib/mt-uptime/keys` | Losing this is unrecoverable — see above |
| `ASPNETCORE_URLS` | `http://+:8080` | Map a host port instead of changing this |
| `Engine__MaxConcurrentChecks` | auto — `clamp(cores × 4, 8, 32)` | Cap on simultaneous checks across all monitors |
| `Engine__RawRetentionDays` | `30` | Raw heartbeats kept before rollup and pruning |
| `Engine__HourlyRetentionDays` | `180` | Daily rollups are kept indefinitely — one tiny row per day |
| `DOTNET_gcServer` | `0` | Workstation GC keeps memory small on a 1 vCPU / 1 GB host |
| `TZ` | `UTC` | Timestamps are stored in UTC regardless |

**Never put application secrets here.** SendGrid keys, database passwords, webhook URLs and bot
tokens are entered in the UI and stored encrypted, using the keys in the volume. Passing them as
environment variables moves them from encrypted-at-rest into `docker inspect` output and your shell
history, which is the opposite of what the design is for.

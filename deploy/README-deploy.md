# Deploying MT-Uptime (Linux, no Docker)

Target: a small Linux VM. It runs comfortably on 1 vCPU / 1 GB.

`provision.sh` is **Debian/Ubuntu only** — every package step is `apt-get`, and it refuses immediately on
anything else rather than half-provisioning a host it cannot finish. It has been run end to end on
Ubuntu 26.04. The application itself is not distro-specific: on Fedora, RHEL, Amazon Linux or anything
else, follow sections 1–6 below, which are the manual equivalent and differ only in the package
commands.
Examples below use **uptime.example.com**; substitute your own hostname.

## The short version

Two scripts do everything in sections 1–6. On your machine:

```bash
./scripts/build-and-package.sh      # or .\scripts\build-and-package.ps1 on Windows
scp build/mt-uptime.tar.gz you@server:~
```

Then on the server, once:

```bash
tar -xzf mt-uptime.tar.gz && cd deploy
sudo ./provision.sh uptime.example.com    # user, runtime, nginx, systemd unit, state dir
sudo ./deploy-on-server.sh ~/mt-uptime.tar.gz
sudo certbot --nginx -d uptime.example.com

# The wizard asks for a one-time token, generated on first start:
sudo cat /var/lib/mt-uptime/setup-token
```

Then open `https://uptime.example.com/` and complete the wizard, pasting that token. It exists because
the wizard mints an administrator and cannot require a login — it runs before any account exists — so
without it whoever reaches the page first between deploy and your finishing the form becomes the
administrator. Requesting a certificate publishes the hostname to Certificate Transparency logs, which
are scanned within seconds, so "nobody knows this host yet" is not true. The token is destroyed once
your account exists.

And for every deploy after that, just the last two lines — `deploy-on-server.sh` swaps the build
atomically, keeps the previous one as `publish.old` for rollback, and fails loudly if `/healthz` does not
come back.

**On a host that already runs something else,** `provision.sh` will stop rather than disturb it: it
refuses to install the .NET runtime if apt would remove packages to do so (see section 1), and it leaves
an existing nginx alone rather than upgrading and restarting it. Build with `--self-contained` and that
first hazard disappears — nothing needs installing on the server at all.

The rest of this document is the manual equivalent, worth reading once so you know what the scripts did.

## 0. Prerequisites

- **DNS:** an `A` (and optional `AAAA`) record for `uptime.example.com` pointing at the instance's public IP. Certbot will not issue a certificate until this resolves.
- **Security groups:**
  - Inbound to EC2: `80`, `443` (public), `22` (your admin IP only).
  - Outbound from EC2: `443` (HTTPS to targets **and** `api.sendgrid.com`), plus whatever the monitors need — DB ports `3306`/`5432`, DNS `53`, arbitrary HTTP/S.
  - The monitored databases' own inbound rules must allow this instance's IP/SG.
  - **Push monitors reverse the direction:** the monitored job calls *in* to this instance on `443`. That is already covered by the public inbound rule above — no extra rule needed — but if you ever narrow inbound `443` to specific sources, every host running a push job must stay on the allowlist or its monitor will alert as down.
- **SendGrid:** an API key and a **verified sender** (single-sender or authenticated domain) for the `From` address.

## 1. Install the ASP.NET Core 10 runtime

Framework-dependent deploy — install the runtime, not the whole SDK.

**Simulate before you install.** On Ubuntu 24.04, `aspnetcore-runtime-10.0` conflicts with
`dotnet-host-8.0` — so apt satisfies the request by *removing* `/usr/bin/dotnet`, and every other .NET
application on the host stops working. There is no warning beyond the package list scrolling past.

```bash
sudo apt-get update
apt-get install -s -y aspnetcore-runtime-10.0 | grep '^Remv'   # anything here = STOP
```

If that prints nothing, install normally:

```bash
# Ubuntu example (see https://learn.microsoft.com/dotnet/core/install/linux for your distro)
sudo apt-get install -y aspnetcore-runtime-10.0
dotnet --list-runtimes    # confirm Microsoft.AspNetCore.App 10.0.x
```

If it prints removals, skip this section entirely and build self-contained instead — the tarball then
carries its own runtime and the host needs none:

```bash
./scripts/build-and-package.sh --self-contained
```

`provision.sh` runs the same simulation and refuses rather than proceeding, so on a shared host it will
stop and tell you this instead of breaking the other services. Re-run it as
`sudo SKIP_RUNTIME=1 ./provision.sh <hostname>` to do everything else and leave the packages alone.

## 2. Service user and directories

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin mtuptime
sudo mkdir -p /opt/mt-uptime
# /var/lib/mt-uptime is created automatically by systemd StateDirectory=, owned by mtuptime.
```

## 3. Publish (on your dev machine) and copy up

```bash
dotnet publish SelfHost.MT-Uptime -c Release -o ./publish
# copy ./publish/* to the instance's /opt/mt-uptime (scp/rsync), then:
sudo chown -R mtuptime:mtuptime /opt/mt-uptime
```

## 4. Install and start the systemd service

The unit listens on **127.0.0.1:5081** — deliberately not 5000, the ASP.NET Core default, which is
usually already taken on a host running anything else .NET. Check 5081 is free here too before you
start, because a port clash shows up as a service that restarts forever rather than as an obvious error:

```bash
ss -ltnp | grep :5081 || echo "5081 free"
```

If something holds it, pick another port and set `ASPNETCORE_URLS` in
`/etc/mt-uptime/mt-uptime.env` (see `mt-uptime.env.example`) — then change `proxy_pass` in the nginx
site in section 5 to match. Do not edit the shipped unit; the env file survives redeploys.

```bash
sudo cp deploy/mt-uptime.service /etc/systemd/system/mt-uptime.service
sudo systemctl daemon-reload
sudo systemctl enable --now mt-uptime
systemctl status mt-uptime          # should be active (running)
journalctl -u mt-uptime -f          # live logs; watch the DB migrate on first boot
curl -fsS http://127.0.0.1:5081/healthz && echo   # -> Healthy
```

## 5. Nginx reverse proxy

```bash
sudo apt-get install -y nginx
sudo cp deploy/mt-uptime.nginx.conf.sample /etc/nginx/sites-available/mt-uptime
sudo ln -s /etc/nginx/sites-available/mt-uptime /etc/nginx/sites-enabled/mt-uptime
sudo rm -f /etc/nginx/sites-enabled/default
sudo nginx -t && sudo systemctl reload nginx
```

## 6. TLS via Let's Encrypt

```bash
sudo apt-get install -y certbot python3-certbot-nginx
sudo certbot --nginx -d uptime.example.com
# certbot rewrites the site to add :443 + a HTTP->HTTPS redirect, and installs a renewal timer.
```

Browse to **https://uptime.example.com/** — the live dashboard should update in real time (proves the WebSocket upgrade works through Nginx).

## 7. Swap (recommended on a 1 GB micro)

```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

## Push / heartbeat monitors

A push monitor is the passive, inverted case: nothing is probed from here. Instead the monitored job
calls in on its own schedule, and the monitor alerts when a ping fails to arrive (a dead-man's switch —
good for cron jobs, backups, ETL runs).

Create the monitor in the dashboard, then have the job call its URL on success:

```bash
curl -fsS https://uptime.example.com/ping/<token> > /dev/null
```

`GET`, `POST`, and `HEAD` all work, so any cron/curl/wget/PowerShell one-liner will do. The monitor goes
down once `IntervalSeconds + GraceSeconds` elapse with no ping.

- **The token is the credential.** The route is anonymous by design. Treat the URL as a secret — note it
  lands in shell history, cron mail, and proxy/access logs, so avoid pasting it into shared logs or CI output.
- **Rate limit:** 120 requests/minute per client IP, returning `429` with `Retry-After`. Legitimate
  pinging is nowhere near this, but if many push jobs share one NAT egress IP, count them against the cap.

## Backup and restore

`/var/lib/mt-uptime` holds the SQLite database **and** the Data Protection keys that decrypt every
secret inside it. They are one unit. A database without its keys starts, migrates and reports healthy
while unable to read a single stored credential — silently — so backing up only the `.db` file leaves
you with something that looks like a backup and is not one.

```bash
# A directory only root can enter. NOT /tmp: the archive contains the key ring, and on a shared host a
# readable copy of that decrypts every stored secret and allows auth-cookie forgery.
sudo install -d -m 0700 /var/backups/mt-uptime

# Stopping first is what makes the snapshot consistent: SQLite checkpoints the WAL into the database on
# a clean shutdown, so you capture one file rather than a database plus a half-applied log.
sudo systemctl stop mt-uptime
sudo tar czf /var/backups/mt-uptime/mt-uptime-$(date -u +%Y%m%dT%H%M%SZ).tar.gz -C /var/lib mt-uptime
sudo systemctl start mt-uptime

# Recursive rather than a glob: `sudo chmod 600 /var/backups/mt-uptime/*.tar.gz` looks equivalent and
# fails, because your shell expands the glob as *you* before sudo runs, and you cannot list a 0700
# root-owned directory. It reports "No such file or directory" for a file that is sitting right there.
sudo chmod -R go-rwx /var/backups/mt-uptime
```

The stop/start costs a few seconds of monitoring. If you would rather not stop the service, use
`/admin/backup` in the UI instead — it uses SQLite's online-backup API and is safe against a live
writer — but note that it captures **the database only**, so you must archive `keys/` separately and
keep the two together.

Then get it off the box. A backup that only exists on the machine it protects is not a backup:

```bash
scp -i <key.pem> user@host:/var/backups/mt-uptime/mt-uptime-<stamp>.tar.gz .
```

### Restoring

```bash
sudo systemctl stop mt-uptime
sudo tar xzf mt-uptime-<stamp>.tar.gz -C /var/lib
sudo chown -R mtuptime:mtuptime /var/lib/mt-uptime   # the service user from section 2 — no hyphen
sudo systemctl start mt-uptime
```

**Perform a restore once, deliberately, before you need one** — ideally onto a scratch machine. Until it
has been done, a backup is a hypothesis. The specific thing worth confirming is not that the app starts,
but that a stored secret still decrypts: open a notification channel and press **Send test**. If the key
ring came back with the database, it sends; if it did not, the app will look perfectly healthy and the
send will fail.

## Account recovery when email is unavailable

Password reset sends a link to the address on the admin account, so it needs **both** an email set on the
profile **and** working SendGrid credentials. If neither is available — no email set, bad API key, or the
sender was never verified — the account cannot be recovered through the browser.

The escape hatch is to delete the single admin row: the app then treats the next start as first-run and
shows the setup wizard again. **Monitors, history, and the Data Protection keys are all preserved** — only
the login is recreated.

> **The wizard is gated by a one-time token, so this is safe to do on a live host.** On finding no
> accounts, the app writes a fresh token to `/var/lib/mt-uptime/setup-token` (mode 0600) and prints it to
> the log. Completing the wizard requires it, so an internet-facing instance in first-run state cannot be
> claimed by whoever reaches `/setup` first. Without that gate this procedure would hand a populated
> instance — keys included — to any passer-by who noticed the redirect to `/setup`.

```bash
sudo systemctl stop mt-uptime
sudo -u mtuptime sqlite3 /var/lib/mt-uptime/mt-uptime.db "DELETE FROM Users;"
sudo systemctl start mt-uptime

# The new setup token. Straight off disk is exact; the log form has to extract it, because journald
# renders the whole message on a single line and `grep -A2` shows the next two log entries instead.
sudo cat /var/lib/mt-uptime/setup-token
sudo journalctl -u mt-uptime --since "1 min ago" | grep -oP 'one-time token:\s+\K[0-9a-f]+' | tail -1
```

Then browse to the dashboard and complete the wizard, pasting that token. Set an email address this time
so reset works. The token is destroyed as soon as the account is created.

> Note: `/auth/forgot` deliberately answers identically whether or not the address exists, so a broken mail
> setup looks the same as success in the browser. It logs an error when delivery fails — check
> `journalctl -u mt-uptime` if a reset link never arrives.

## Notes

- **Backups:** back up `/var/lib/mt-uptime/` — it holds both the SQLite database **and** the Data Protection keys. If the keys are lost, all encrypted secrets (SendGrid API key, monitored-DB passwords) become undecryptable and every login cookie is invalidated.
- **Redeploys:** overwrite `/opt/mt-uptime` and `sudo systemctl restart mt-uptime`. The database and keys in `/var/lib/mt-uptime` are untouched; pending EF migrations apply automatically on startup.
- **Config overrides:** the service file sets `Storage__DatabasePath` and `Storage__DataProtectionKeysPath` to `/var/lib/mt-uptime/…`. Adjust via `systemctl edit mt-uptime` if needed.

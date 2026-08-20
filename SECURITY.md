# Security

## Reporting a vulnerability

Email **security@melssontechnology.com** with a description, affected version, and reproduction steps.
Please do not open a public issue for anything exploitable.

You should get an acknowledgement within **3 working days** and an assessment within **10**. If a fix is
warranted we will agree a disclosure timeline with you, and credit you unless you'd rather we didn't.

---

## Threat model — please read before reporting

Several behaviours below look alarming out of context and are working as intended. Knowing which is
which saves everyone time.

### The Admin role is fully trusted

MT-Uptime has three roles — **Admin**, **Editor** and **Viewer**. Anyone holding **Admin** can:

- read and modify every monitor, notification channel and setting
- manage accounts, including setting another user's password
- download the **entire database** via `/admin/backup`, including encrypted secrets
- cause outbound network connections to hosts of their choosing (see below)

**Do not give Admin to anyone you would not give database access to.** Reports amounting to "an
authenticated Admin can do administrative things" are not vulnerabilities.

**What the roles are, and are not.** Editor and Viewer exist so a team can share an instance without
everyone holding the keys: Editor manages monitors, channels and status pages but not accounts, settings
or backups; Viewer is read-only. That boundary is enforced by authorization policies on every page and
endpoint, and it is a boundary we will fix bugs in — a Viewer reaching an Editor or Admin capability is
a vulnerability, please report it.

It is **not** a hostile-user boundary. Editors legitimately configure monitors, which means causing
outbound connections to hosts of their choosing, and they can see the targets and response bodies of
everything the instance watches. Treat Editor as "a colleague you trust with your infrastructure", not
as a sandbox.

### Monitors connect wherever you tell them to

This is the core function of the product, so it cannot be locked down without breaking it:

- **TCP monitors** connect to any host on any port from 1–65535.
- **DNS monitors** query an arbitrary resolver address that you supply.
- **HTTP monitors** request any URL, with an option to ignore TLS certificate errors.
- **MySQL / PostgreSQL monitors** open real database connections with credentials you supply.

Consequently an authenticated user can reach hosts the server can reach but they cannot — including RFC
1918 addresses, `localhost`, and cloud metadata endpoints such as `169.254.169.254`. On a single-operator
install this is exactly the intent: you are monitoring your own private infrastructure.

**If you are running this for anyone other than yourself, you must add egress restrictions** — a network
policy, an egress proxy, or a blocklist for private and link-local ranges. The application does not do
this for you, because for its intended use it would be wrong to.

### The push endpoint is anonymous by design

`/ping/{token}` accepts unauthenticated `GET`, `POST` and `HEAD`. The 128-bit random token in the URL
*is* the credential, because the cron jobs that call it have no session to present. Treat the URL as a
secret: it lands in shell history, cron mail, and proxy logs. It is rate limited to 120 requests per
minute per client IP.

Because it travels in URLs and logs, the ping token is deliberately **not** treated as a secret at rest
— it is stored in plaintext in the monitor's configuration, unlike the credentials listed under "Secrets
at rest" below. Anyone who can read the database can forge that monitor's heartbeats. Only Editors and
Admins are shown the ping URL, and it can be rotated from the monitor editor if it leaks.

The monitor export (`/admin/export/monitors`) blanks it, along with every other credential field, because
that file is meant to leave the instance. The database backup necessarily still contains it — a backup is
a byte copy — so **treat a backup as the credentials it carries**, not merely as data.

### The first run is gated by a token

The setup wizard creates an administrator and cannot require a login, so an empty account table alone is
not authorization for it. On finding no accounts, MT-Uptime writes a one-time token to `setup-token` in
the data directory (mode 0600) and prints it to the log; completing the wizard requires it, and it is
destroyed once the account exists. Without that, whoever reached `/setup` first on a newly-deployed or
recovered instance would own it.

### Reverse-proxy assumptions

`X-Forwarded-For` and `X-Forwarded-Proto` are trusted **from loopback only**, which is the documented
deployment: Kestrel binds `127.0.0.1` and nginx is the only possible sender. A forwarded header from
anywhere else is ignored, because it is indistinguishable from a value the client chose — and the rate
limiters partition on the resulting address, so trusting it would let one caller occupy unlimited
buckets.

If your proxy is not on loopback — nginx in another container, a load balancer — declare it, or the
limiters will see the proxy as the only client and throttle everyone together:

```
ForwardedHeaders__KnownProxies=203.0.113.7,172.18.0.0/16
```

Set `App__PublicBaseUrl` to the instance's public address as well. Links in password-reset emails are
built from it; left unset they fall back to the request's `Host` header, which the caller controls.
Setting it also narrows `AllowedHosts` from `*` to that hostname.

---

## What we do consider a vulnerability

- Anything reachable **without authentication** that shouldn't be. Endpoint authorization is covered by
  automated tests (`Tests.MT-Uptime/EndpointAuthorizationTests.cs`) precisely because a regression here is
  serious — an earlier build shipped `/auth/profile` unauthenticated, which allowed account takeover in
  two requests.
- Bypassing the login: authentication bypass, session fixation, cookie forgery.
- Stored secrets recoverable **without** the Data Protection keys.
- Password-reset weaknesses: token prediction, reuse after use, expiry not enforced, or the reset endpoint
  revealing whether an address has an account.
- Cross-site scripting, CSRF on a state-changing endpoint, or SQL injection.
- Anything letting an **unauthenticated** caller cause outbound connections or resource exhaustion.

## Secrets at rest

Sensitive settings — SendGrid API key, monitored database passwords, monitored-endpoint credentials and
custom headers, webhook URLs, bot tokens — are encrypted with ASP.NET Core Data Protection before being
written to the database. The keys live outside the database (`/var/lib/mt-uptime/keys` in the documented
deployment).

Three consequences worth stating plainly:

1. A stolen database alone does not yield **those** secrets.
2. It does yield **push monitor ping tokens**, which are stored in plaintext by design — see "The push
   endpoint is anonymous by design" above. It also yields password *hashes*, which are PBKDF2 and not
   directly usable, but are worth a strong password nonetheless.
3. **Losing the keys is unrecoverable.** Back them up with the database, never separately.

## Password-reset links in logs

A reset link carries its token in the query string, so anything that logs full request lines records a
live credential. The shipped nginx site therefore defines a scrubbed log format that logs the path
without the query string and drops the Referer; `provision.sh` writes that same file. If you are running
behind your own proxy, do the equivalent there — the token is valid for an hour and log retention is
usually much longer.

## Sessions

The authentication cookie carries a session stamp that is re-checked against the account on every
request, and every 30 seconds inside an open interactive circuit. Deleting an account, changing or
setting its password, completing a password reset, and changing its role all end that account's existing
sessions immediately.

Upgrading to a build that introduced this signs everyone out once, because cookies issued earlier carry
no stamp to check.

## Supported versions

This project is pre-1.0. Security fixes land on `main`; there are no maintained release branches yet.

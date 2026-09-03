#!/usr/bin/env python3
"""fixture-server.py — the HTTP target the E2E battery probes.

Runs on 127.0.0.1:8090 under mt-uptime-e2e-fixture.service. nginx sits in front of it and republishes
it as plain HTTP on :8081 and as HTTPS on :8443/:8444/:8445/:8446 with four different certificates, so
every TLS prediction is the same backend seen through a different certificate.

Python 3 standard library only, on purpose: this has to survive on a box where the only thing we are
allowed to install is what the monitor types genuinely need. No dependencies means no pip, no venv, and
nothing to go stale between runs.

Routes
    /ok                 200 with the keyword in the body
    /status/<n>         the status code you name, so accepted-status-code ranges can be tested exactly
    /slow?ms=N          sleeps N ms BEFORE the headers, then 200 with the keyword
    /echo               200, a JSON echo of {method, path, headers, body}
    /basic              HTTP Basic; 401 with a challenge unless the credentials match
    /bearer             Authorization: Bearer <token>; 401 otherwise
    /redirect           302 to /ok
    /redirect-loop      302 to itself, for the too-many-redirects path
    /toggle             the one the break/restore helper drives: 503 while the down-flag file exists,
                        slow while the slow-flag file exists, otherwise 200 with the keyword

Configuration is entirely by environment variable, set by the systemd unit from the manifest, so this
file holds no secrets and no host-specific values.
"""

import http.server
import json
import os
import socketserver
import sys
import time
import urllib.parse

HOST = os.environ.get("E2E_FIXTURE_HOST", "127.0.0.1")
PORT = int(os.environ.get("E2E_FIXTURE_PORT", "8090"))
KEYWORD = os.environ.get("E2E_KEYWORD", "MT-UPTIME-E2E-OK")
BASIC_USER = os.environ.get("E2E_BASIC_USER", "e2e")
BASIC_PASS = os.environ.get("E2E_BASIC_PASS", "e2e-pass")
BEARER_TOKEN = os.environ.get("E2E_BEARER_TOKEN", "e2e-bearer-token")
DOWN_FLAG = os.environ.get("E2E_HTTP_TOGGLE_FLAG", "/run/mt-uptime-e2e/http.down")
SLOW_FLAG = os.environ.get("E2E_HTTP_SLOW_FLAG", "/run/mt-uptime-e2e/http.slow")
SLOW_MS = int(os.environ.get("E2E_HTTP_SLOW_MS", "1500"))

# A body large enough to be a real read but small enough to stay legible in a failure message. The
# keyword sits in the middle rather than at the start so a truncated read cannot pass by accident.
BODY_OK = f"<html><body><h1>MT-Uptime E2E fixture</h1><p>{KEYWORD}</p></body></html>"


class Handler(http.server.BaseHTTPRequestHandler):
    # Speak HTTP/1.1 so keep-alive works. HttpChecker pools connections, and a fixture that closed
    # every connection would exercise a code path no real target uses.
    protocol_version = "HTTP/1.1"
    server_version = "MT-Uptime-E2E-Fixture/1.0"

    # --- plumbing ---------------------------------------------------------------------------------

    def log_message(self, fmt, *args):
        """One line per request on stderr, which systemd routes to the journal.

        The default writes to stderr already, but includes a timestamp journald then adds again.
        """
        sys.stderr.write("%s %s\n" % (self.address_string(), fmt % args))

    def _send(self, status, body=b"", content_type="text/html; charset=utf-8", headers=None):
        """The single exit point, so Content-Length and the bodiless-status rules live in one place.

        Two rules that are easy to get wrong and expensive to debug:

        HEAD must carry the same headers as GET and no body. Send one and the client waits for bytes
        that never come, so a HEAD monitor hangs until its timeout instead of failing — a much harder
        thing to diagnose than a wrong status code.

        1xx, 204 and 304 must carry no body AND no Content-Length. `/status/204` is a legitimate thing
        for a monitor to accept, and answering it with `Content-Length: 42` desynchronises a keep-alive
        connection: the client waits for a body the framing forbids, and the *next* request on that
        connection fails instead of this one. HttpChecker pools connections, so that misattribution
        would land on whichever monitor happened to reuse the socket.
        """
        if isinstance(body, str):
            body = body.encode("utf-8")
        bodiless = status < 200 or status in (204, 304)
        if bodiless:
            body = b""

        self.send_response(status)
        if not bodiless:
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        if self.command != "HEAD" and body:
            self.wfile.write(body)

    def _read_body(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return ""
        return self.rfile.read(length).decode("utf-8", errors="replace")

    # --- routes -----------------------------------------------------------------------------------

    def _route(self):
        parsed = urllib.parse.urlsplit(self.path)
        path = parsed.path.rstrip("/") or "/"
        query = urllib.parse.parse_qs(parsed.query)

        if path in ("/", "/ok"):
            return self._send(200, BODY_OK)

        if path.startswith("/status/"):
            try:
                code = int(path.rsplit("/", 1)[1])
            except ValueError:
                return self._send(400, "status must be a number")
            if not 100 <= code <= 599:
                return self._send(400, "status out of range")
            # Body carries the keyword too, so a status test and a keyword test can share the route.
            return self._send(code, f"<html><body>{code} {KEYWORD}</body></html>")

        if path == "/slow":
            ms = int(query.get("ms", ["1000"])[0])
            # Before the response line, deliberately. HttpChecker measures to the headers
            # (HttpCompletionOption.ResponseHeadersRead), so sleeping after them would produce a fast
            # ResponseTimeMs and every Degraded prediction would silently stop testing anything.
            time.sleep(min(ms, 60_000) / 1000.0)
            return self._send(200, BODY_OK)

        if path == "/echo":
            payload = {
                "method": self.command,
                "path": self.path,
                # Header names are lower-cased for a stable comparison; values are kept verbatim.
                "headers": {k.lower(): v for k, v in self.headers.items()},
                "body": self._read_body(),
            }
            return self._send(
                200, json.dumps(payload, indent=2), content_type="application/json; charset=utf-8"
            )

        if path == "/basic":
            import base64

            expected = "Basic " + base64.b64encode(
                f"{BASIC_USER}:{BASIC_PASS}".encode("utf-8")
            ).decode("ascii")
            if self.headers.get("Authorization") != expected:
                return self._send(
                    401,
                    "unauthorized",
                    headers={"WWW-Authenticate": 'Basic realm="mt-uptime-e2e"'},
                )
            return self._send(200, BODY_OK)

        if path == "/bearer":
            if self.headers.get("Authorization") != f"Bearer {BEARER_TOKEN}":
                return self._send(
                    401, "unauthorized", headers={"WWW-Authenticate": "Bearer"}
                )
            return self._send(200, BODY_OK)

        if path == "/redirect":
            return self._send(302, "", headers={"Location": "/ok"})

        if path == "/redirect-loop":
            return self._send(302, "", headers={"Location": "/redirect-loop"})

        if path == "/toggle":
            # The flag files are what `mt-uptime-e2e-target break http` creates. Checked per request
            # rather than cached, because the whole point is that the state changes underneath us
            # while a monitor is running.
            if os.path.exists(DOWN_FLAG):
                return self._send(503, "<html><body>503 service unavailable</body></html>")
            if os.path.exists(SLOW_FLAG):
                time.sleep(SLOW_MS / 1000.0)
            return self._send(200, BODY_OK)

        return self._send(404, "<html><body>404 not found</body></html>")

    def do_GET(self):
        self._route()

    def do_HEAD(self):
        self._route()

    def do_POST(self):
        self._route()

    def do_PUT(self):
        self._route()

    def do_DELETE(self):
        self._route()


class Server(socketserver.ThreadingTCPServer):
    """Threaded, and that is load-bearing rather than a default worth accepting without thought.

    /slow and the slow half of /toggle block for well over a second. Single-threaded, one slow probe
    would stall every other monitor's request behind it, and the Degraded scenario would make the HTTP,
    TLS and keyword scenarios look intermittently slow too — an entirely self-inflicted flake.
    """

    daemon_threads = True
    allow_reuse_address = True


def main():
    with Server((HOST, PORT), Handler) as httpd:
        sys.stderr.write(f"mt-uptime-e2e fixture listening on {HOST}:{PORT}\n")
        sys.stderr.flush()
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()

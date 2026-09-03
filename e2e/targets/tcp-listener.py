#!/usr/bin/env python3
"""tcp-listener.py — a TCP port that accepts connections, for the Tcp monitor type.

Runs on 127.0.0.1:8082 under mt-uptime-e2e-tcp.service. `mt-uptime-e2e-target break tcp` stops the
unit, which is what turns the port from "accepting" into "connection refused".

TcpChecker only calls ConnectAsync and closes, so nothing needs to be spoken. A banner is written
anyway, because it makes `nc 127.0.0.1 8082` a one-command way for a human to tell "the fixture is
listening" from "something else grabbed the port" — which has happened, and looks identical from the
monitor's side.

socat could do this in one line. A unit file of its own is preferred because the break/restore helper
drives it by unit name, and `socat` in an ExecStart is one more package the box has to have for a
reason that is not a monitor type.
"""

import os
import socketserver
import sys

HOST = os.environ.get("E2E_TCP_HOST", "127.0.0.1")
PORT = int(os.environ.get("E2E_TCP_PORT", "8082"))
BANNER = os.environ.get("E2E_TCP_BANNER", "MT-UPTIME-E2E-TCP\n")


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            self.request.sendall(BANNER.encode("utf-8"))
        except OSError:
            # A checker that connects and immediately closes is the normal case, not an error: it has
            # already learned what it came to learn. Swallowing this keeps the journal readable.
            pass


class Server(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    with Server((HOST, PORT), Handler) as srv:
        sys.stderr.write(f"mt-uptime-e2e tcp listener on {HOST}:{PORT}\n")
        sys.stderr.flush()
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()

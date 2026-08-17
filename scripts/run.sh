#!/usr/bin/env bash
#
# run.sh — start MT-Uptime locally on http://localhost:5081
#
# First run creates the database and shows the setup wizard. Data lives in
# SelfHost.MT-Uptime/App_Data/ (git-ignored) — delete that directory to start over, but note it holds
# the Data Protection keys as well as the database, so deleting it discards any stored secrets too.
#
#   ./scripts/run.sh                     # Development
#   ./scripts/run.sh -c Release          # extra arguments pass through to `dotnet run`

set -euo pipefail

ENGINE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Starting MT-Uptime -> http://localhost:5081  (Ctrl+C to stop)"
exec dotnet run --project "$ENGINE/SelfHost.MT-Uptime" "$@"

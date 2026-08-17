#!/usr/bin/env bash
#
# test.sh — run the full test suite.
#
# The suite is hermetic: throwaway SQLite files in the temp directory, no external services, no
# environment variables. A fresh clone with only the .NET SDK installed can run this immediately.
#
#   ./scripts/test.sh              # all tests
#   ./scripts/test.sh --filter X   # any extra arguments pass through to `dotnet test`

set -euo pipefail

ENGINE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

exec dotnet test "$ENGINE/MT-Uptime.Engine.slnx" "$@"

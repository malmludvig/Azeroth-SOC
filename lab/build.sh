#!/usr/bin/env bash
# Build the lab images without starting anything. The orchestrator
# (src/Game.Cli) needs them to exist.
set -euo pipefail
cd "$(dirname "$0")"
docker compose build

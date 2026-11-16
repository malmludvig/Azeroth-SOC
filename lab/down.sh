#!/usr/bin/env bash
# Stop the lab and remove its containers and networks.
set -euo pipefail
cd "$(dirname "$0")"
docker compose down --remove-orphans

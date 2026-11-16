#!/usr/bin/env bash
# Build and start the lab, then add the routes that send traffic between
# the plains and the DMZ through gate. The routes are added from the host
# with a privileged exec, so the lab containers themselves never hold
# NET_ADMIN (only gate does).
set -euo pipefail
cd "$(dirname "$0")"

export LAB_NET_DRIVER="${LAB_NET_DRIVER:-macvlan}"
if ! docker compose up -d --build; then
  if [ "$LAB_NET_DRIVER" = "macvlan" ]; then
    echo "Start with macvlan networks failed. Retrying with bridge networks."
    docker compose down --remove-orphans >/dev/null 2>&1 || true
    export LAB_NET_DRIVER=bridge
    docker compose up -d --build
  else
    exit 1
  fi
fi

route() { # container, destination subnet, next hop
  docker exec --privileged -u 0 "azeroth-lab-$1-1" ip route replace "$2" via "$3"
}

for host in citizens garrison defias probe; do
  route "$host" 10.20.0.0/24 10.10.0.2
done
route web 10.10.0.0/24 10.20.0.2

echo "Lab is up on $LAB_NET_DRIVER networks. Shell in the gate: docker exec -it azeroth-lab-gate-1 bash"

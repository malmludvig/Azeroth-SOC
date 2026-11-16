#!/usr/bin/env bash
# Plan step 1: verify that the lab behaves as the design assumes on this machine.
# Run lab/up.sh first. Exit code 0 means every check passed.
#
# Checks:
#   1. Routing     plains hosts reach web through gate
#   2. Firewall    nftables in gate with default deny, verified by real traffic
#   3. Rate limit  per-source limiting in nftables
#   4. Capture     tcpdump works in gate
#   5. Isolation   no container reaches the internet or the Docker socket,
#                  and capabilities are as small as declared
set -uo pipefail

PROJECT=azeroth-lab
WEB=10.20.0.10
GARRISON=10.10.0.20
ALL="gate web citizens garrison defias probe"

pass=0
fail=0
ok() { echo "  PASS  $1"; pass=$((pass + 1)); }
ko() { echo "  FAIL  $1"; fail=$((fail + 1)); }

x() { # run a command in a lab container
  local name=$1
  shift
  docker exec "${PROJECT}-${name}-1" "$@"
}

port_open() { x "$1" nc -z -w 2 "$2" "$3" >/dev/null 2>&1; }
expect_open() { if port_open "$1" "$2" "$3"; then ok "$4"; else ko "$4"; fi; }
expect_closed() { if port_open "$1" "$2" "$3"; then ko "$4"; else ok "$4"; fi; }

http_code() { x "$1" curl -s -o /dev/null -m 3 -w '%{http_code}' "http://${WEB}/" 2>/dev/null; }

load_rules() { docker exec -i "${PROJECT}-gate-1" nft -f -; }

for name in $ALL; do
  if [ "$(docker inspect -f '{{.State.Running}}' "${PROJECT}-${name}-1" 2>/dev/null)" != "true" ]; then
    echo "Container ${PROJECT}-${name}-1 is not running. Run lab/up.sh first."
    exit 2
  fi
done

echo "Docker: $(docker version --format '{{.Server.Version}} {{.Server.Os}}/{{.Server.Arch}}' 2>/dev/null)"
echo "Kernel: $(x gate uname -r 2>/dev/null)"
driver=$(docker network inspect -f '{{.Driver}}' "${PROJECT}_plains" 2>/dev/null)
echo "Lab networks: $driver"

echo
echo "1. Routing through gate (no rules loaded)"
x gate nft flush ruleset
for port in 80 22 8080 3306; do
  expect_open probe "$WEB" "$port" "probe reaches web:$port"
done
if [ "$(http_code citizens)" = "200" ]; then ok "citizens get HTTP 200"; else ko "citizens get HTTP 200"; fi
if [ "$fail" -gt 0 ] && [ "$driver" = "bridge" ]; then
  echo "  NOTE  On bridge networks the Docker host drops routed traffic when it"
  echo "        filters bridged packets. Use macvlan, or run the lab in a Linux VM."
fi

echo
echo "2. Firewall: default deny, web open to all, management from garrison only"
if load_rules <<EOF
flush ruleset
table inet gate {
  chain forward {
    type filter hook forward priority 0; policy drop;
    ct state established,related accept
    ip saddr ${GARRISON} ip daddr ${WEB} tcp dport 22 accept
    ip daddr ${WEB} tcp dport 80 accept
  }
}
EOF
then ok "nft accepts the ruleset"; else ko "nft accepts the ruleset"; fi
expect_open probe "$WEB" 80 "probe reaches web:80"
for port in 22 8080 3306; do
  expect_closed probe "$WEB" "$port" "probe is blocked from web:$port"
done
expect_open garrison "$WEB" 22 "garrison reaches web:22"
expect_closed defias "$WEB" 22 "defias is blocked from web:22"
if [ "$(http_code citizens)" = "200" ]; then ok "citizens still get HTTP 200"; else ko "citizens still get HTTP 200"; fi

echo
echo "3. Rate limiting per source address"
if load_rules <<EOF
flush ruleset
table inet gate {
  set flood {
    type ipv4_addr
    flags dynamic
    timeout 30s
  }
  chain forward {
    type filter hook forward priority 0; policy drop;
    ct state established,related accept
    ip saddr ${GARRISON} ip daddr ${WEB} tcp dport 22 accept
    ip daddr ${WEB} tcp dport 80 ct state new add @flood { ip saddr limit rate over 5/second burst 5 packets } drop
    ip daddr ${WEB} tcp dport 80 accept
  }
}
EOF
then ok "nft accepts a dynamic set with a per-source limit"; else ko "nft accepts a dynamic set with a per-source limit"; fi
burst=$(x defias sh -c "for i in \$(seq 40); do curl -s -o /dev/null -m 1 -w '%{http_code}\n' http://${WEB}/ & done; wait" 2>/dev/null | grep -c '^200')
if [ "$burst" -ge 1 ] && [ "$burst" -le 20 ]; then
  ok "defias burst of 40 is limited ($burst got through)"
else
  ko "defias burst of 40 is limited ($burst got through)"
fi
calm=0
for i in 1 2 3 4 5; do
  [ "$(http_code citizens)" = "200" ] && calm=$((calm + 1))
  sleep 1
done
if [ "$calm" -eq 5 ]; then ok "citizens at 1 request/s are unaffected ($calm/5)"; else ko "citizens at 1 request/s are unaffected ($calm/5)"; fi

echo
echo "4. Packet capture in gate"
(x gate timeout 8 tcpdump -Z root -c 1 -n -i any "tcp port 80 and host ${WEB}" >/dev/null 2>&1) &
capture=$!
sleep 2
http_code probe >/dev/null
if wait "$capture"; then ok "tcpdump sees traffic passing gate"; else ko "tcpdump sees traffic passing gate"; fi

echo
echo "5. Isolation"
for name in $ALL; do
  if x "$name" nc -z -w 3 1.1.1.1 443 >/dev/null 2>&1; then
    ko "$name cannot reach the internet"
  else
    ok "$name cannot reach the internet"
  fi
  if x "$name" test -e /var/run/docker.sock; then
    ko "$name has no Docker socket"
  else
    ok "$name has no Docker socket"
  fi
done
caps() { x "$1" sh -c "grep CapEff /proc/1/status | cut -f2"; }
if [ "$(caps gate)" = "0000000000003000" ]; then
  ok "gate holds only NET_ADMIN and NET_RAW"
else
  ko "gate holds only NET_ADMIN and NET_RAW (CapEff=$(caps gate))"
fi
for name in citizens garrison defias probe; do
  if [ "$(caps "$name")" = "0000000000000000" ]; then
    ok "$name holds no capabilities"
  else
    ko "$name holds no capabilities (CapEff=$(caps "$name"))"
  fi
done

x gate nft flush ruleset

echo
echo "Result: $pass passed, $fail failed"
[ "$fail" -eq 0 ]

#!/bin/sh
set -e
# The orchestrator moves the two forgotten services between runs.
DEBUG_PORT="${DEBUG_PORT:-8080}"
DB_PORT="${DB_PORT:-3306}"
sed -i "s/listen 8080 /listen ${DEBUG_PORT} /" /etc/nginx/http.d/default.conf
# The word of the day on the board, a new one for every run.
sed -i "s/@WORD@/${BOARD_WORD:-lantern}/" /var/www/board/index.html
/usr/sbin/sshd -e
socat "TCP-LISTEN:${DB_PORT},fork,reuseaddr" SYSTEM:'echo mock-db' &
exec nginx -g 'daemon off;'

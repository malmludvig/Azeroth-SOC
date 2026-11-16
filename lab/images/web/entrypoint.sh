#!/bin/sh
set -e
/usr/sbin/sshd -e
socat TCP-LISTEN:3306,fork,reuseaddr SYSTEM:'echo mock-db' &
exec nginx -g 'daemon off;'

# gate: the router/firewall the player gets a shell in.
FROM alpine:3.20
RUN apk add --no-cache bash nftables tcpdump iproute2 iputils curl netcat-openbsd
COPY quest /usr/local/bin/quest
RUN chmod +x /usr/local/bin/quest
WORKDIR /root
CMD ["sleep", "infinity"]

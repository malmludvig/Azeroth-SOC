# web: the town notice board in the DMZ.
# 80 public site, 22 management, 8080 forgotten debug page, 3306 mock database.
FROM alpine:3.20
RUN apk add --no-cache nginx openssh-server socat netcat-openbsd iproute2 \
 && ssh-keygen -A \
 && mkdir -p /run/nginx /var/www/board /var/www/debug
COPY nginx.conf /etc/nginx/http.d/default.conf
COPY board.html /var/www/board/index.html
COPY debug.html /var/www/debug/index.html
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 22 80 3306 8080
ENTRYPOINT ["/entrypoint.sh"]

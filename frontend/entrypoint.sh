#!/bin/sh
set -eu
# HTTPS origins always require Secure cookies. HTTP loopback remains usable by
# default; non-loopback HTTP is accepted only with the explicit development
# acknowledgement validated by server-config.ts.
case "${PUBLIC_ORIGIN:-}" in
    https://*) SECURE_COOKIES=true ;;
    "") [ "${SECURE_COOKIES+x}" = x ] || SECURE_COOKIES=false ;;
    *) [ "${SECURE_COOKIES+x}" = x ] || SECURE_COOKIES=false ;;
esac
export SECURE_COOKIES
exec node dist-node/server.js

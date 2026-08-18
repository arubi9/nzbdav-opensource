# NZBDAV frontend

The frontend is the browser-facing server for NZBDAV.

## Development

```bash
npm ci
export SESSION_KEY=local-development-session-fixture
export FRONTEND_BACKEND_API_KEY=local-development-backend-fixture
npm run dev
```

For a local-only development server, keep the listener and published port on
loopback (`127.0.0.1`). Do not expose an HTTP development server directly to a
LAN, VPN, or the public Internet: HTTP cannot protect session cookies in
transit. Use HTTPS (with `Secure` cookies) for any non-loopback deployment and
set the trusted proxy/public-origin settings to match the exact external
origin.

## Build and test

```bash
npm run build
npm run build:server
npm test -- --run
npm run typecheck
```

The production server runs with `npm start` after the build. Production
startup requires explicit `SESSION_KEY` and `FRONTEND_BACKEND_API_KEY` values;
it does not generate deployment credentials or use development fallbacks.

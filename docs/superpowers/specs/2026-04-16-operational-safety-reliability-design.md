# Operational Safety Reliability Design

**Goal**

Tighten the most immediate operational reliability gaps in request handling and container startup without changing the broader streaming architecture.

**Problem**

Three current behaviors are risky in production:

- `RequestTimeoutMiddleware` applies a hard 5-minute lifetime to streaming requests, which can terminate healthy long-running playback.
- `entrypoint.sh` checks the backend migration exit status incorrectly, so a failed migration can log and exit with the wrong code.
- `HealthCheckService` still listens for the obsolete `usenet.host` config key, so changing `usenet.providers` does not clear cached missing-segment state.

**Approaches Considered**

1. Large “reliability sweep” touching timeouts, ranges, Jellyfin cleanup, probe cleanup, and startup scripts at once.

This would fix more issues per pass, but it mixes unrelated behavior changes and raises regression risk.

2. Small operational-safety slice focused on timeout semantics, startup exit handling, and config-listener drift.

This is the recommended approach. It targets the highest operational risks with limited surface area and clear verification points.

3. Stream correctness only.

Fixing only timeout and range handling would leave startup orchestration and config drift untouched, so it is lower value than option 2.

**Selected Design**

- Streaming requests should not use an absolute middleware timeout.
  - Keep the 30-second timeout for metadata/non-streaming requests.
  - Skip replacing `RequestAborted` for recognized streaming endpoints.
- `entrypoint.sh` should capture the migration command exit code into a variable and reuse that variable for both logging and `exit`.
- `HealthCheckService` should listen for `usenet.providers` changes, matching the rest of the provider pipeline.

**Testing**

- Add middleware tests proving streaming requests preserve the original abort token and metadata requests use a wrapped timeout token.
- Add a focused `HealthCheckService` test proving a `usenet.providers` change clears `MissingSegmentIds`.
- Add a lightweight script regression test proving the entrypoint captures and reuses the migration exit code variable.

**Out of Scope**

- Jellyfin `.strm` cleanup
- `416 Range Not Satisfiable` handling
- probe-data cleanup
- broader timeout redesign beyond removing the absolute stream lifetime cap


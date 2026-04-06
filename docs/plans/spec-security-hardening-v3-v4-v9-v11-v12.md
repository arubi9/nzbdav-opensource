# Security Hardening Spec — V3, V4, V9, V11, V12

*Approved 2026-04-06*

---

## V3: DISABLE_WEBDAV_AUTH Confirmation String

**Current:** `DISABLE_WEBDAV_AUTH=true` silently disables all WebDAV auth.

**Change:** Require `DISABLE_WEBDAV_AUTH=I_UNDERSTAND_THIS_IS_INSECURE`. Any other value (including `true`) is rejected with a startup error log explaining the required value. When the confirmation IS set, a `BackgroundService` (`InsecureAuthWarningService`) logs a warning every 60 seconds, cancelled cleanly via `StoppingToken` on shutdown.

**Files:**
- Modify: `backend/Auth/WebApplicationAuthExtensions.cs`
- Create: `backend/Services/InsecureAuthWarningService.cs`
- Modify: `backend/Program.cs` (register hosted service conditionally)

---

## V4: Redact API Keys from Request Logs

**Current:** Kestrel and Serilog log full request URLs including `?apikey=xyz`.

**Change:** Custom Serilog enricher that intercepts both rendered message templates and structured properties (`RequestPath`, `RequestQuery`, full URL). Replaces `apikey=<value>` with `apikey=REDACTED`. Registered in the Serilog pipeline in `Program.cs`. This is defense-in-depth — the primary mitigation is V11 (removing raw keys from .strm files).

**Files:**
- Create: `backend/Logging/ApiKeyRedactionEnricher.cs`
- Modify: `backend/Program.cs` (add `.Enrich.With<ApiKeyRedactionEnricher>()`)

---

## V9: Reject Basic Auth over HTTP

**Current:** `AllowInsecureProtocol = true` permits Basic Auth credentials over plaintext HTTP.

**Change:** `AllowInsecureProtocol = false` by default. New env var `ALLOW_INSECURE_AUTH=true` opts back in for development. **Breaking change** for deployments without TLS.

On startup, if `ALLOW_INSECURE_AUTH` is not set and the server is listening on HTTP (not HTTPS), log: `"Basic auth over HTTP is now disabled by default. If you need plain-HTTP auth for development, set ALLOW_INSECURE_AUTH=true."`

**Files:**
- Modify: `backend/Auth/ServiceCollectionAuthExtensions.cs`

---

## V11: Signed Tokens in .strm Files

**Current:** `.strm` files contain `?apikey=THE_RAW_KEY` — raw API key persisted on Jellyfin filesystem.

**Change:** Use `StreamTokenService.GenerateToken` with 7-day expiry. Refresh when token is older than 3 days.

**Token overlap window:** `StreamTokenService.ValidateToken` accepts tokens whose server-computed expiry is within the last 7 days (one full prior period). Implementation: compute `expected_expiry = parsed_expiry_from_token`, verify `now < expected_expiry + grace_period`. The grace period is the server-side constant `DefaultExpiryMinutes` (7 days = 10080 minutes), NOT the token's claimed expiry. This prevents attackers from extending their window by manipulating the expiry field — the HMAC binds the expiry to the server's key.

**Atomic per-item rotation:** Each `.strm` file is read → token extracted → age checked against server time → regenerated only if stale (> 3 days old) → written. If NZBDAV is unreachable mid-cycle, unprocessed files keep their existing tokens.

**Files:**
- Modify: `backend/Services/StreamTokenService.cs` (add grace period to validation, change default expiry to 7 days)
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs` (use signed tokens, refresh logic)

---

## V12: ASP.NET Core Built-in Rate Limiting

**Current:** No rate limiting on auth failures. Brute-force attempts are unlimited.

**Change:** Use `Microsoft.AspNetCore.RateLimiting` with a fixed-window policy: 10 requests per 60 seconds per IP on auth-failing endpoints. Returns 429 Too Many Requests with `Retry-After: 60` header. In-memory, no database.

**Scope:** Single rate limiter policy covering both `/api/*` REST routes AND the SABnzbd `/api` endpoint under the same IP-keyed counter. Not two separate policies.

**IP extraction:** Add `UseForwardedHeaders()` before `UseRateLimiter()` so the rate limiter sees the real client IP behind HAProxy/Caddy/Nginx, not the proxy's IP.

**Files:**
- Modify: `backend/Program.cs` (add `AddRateLimiter`, `UseForwardedHeaders`, `UseRateLimiter`)
- Modify: `backend/Api/Filters/ApiKeyAuthFilter.cs` (apply rate limiter policy name)
- Modify: `backend/Api/SabControllers/SabApiController.cs` (apply same policy)

---

## Implementation Order

1. V3 (confirmation string) — standalone, no dependencies
2. V4 (log redaction) — standalone
3. V9 (reject HTTP auth) — standalone
4. V12 (rate limiting) — needs ForwardedHeaders before V11 testing
5. V11 (signed tokens in .strm) — depends on StreamTokenService grace period change

## Testing

- V3: unit test that `IsWebdavAuthDisabled()` returns false for `true`, returns true for `I_UNDERSTAND_THIS_IS_INSECURE`
- V4: unit test that enricher redacts `apikey=xyz` from log events
- V9: verify auth middleware rejects Basic over HTTP, accepts over HTTPS (or with opt-in)
- V11: unit test for token grace period validation (expired-but-within-grace accepts, expired-beyond-grace rejects)
- V12: integration test that 11th request from same IP returns 429

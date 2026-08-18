import { createCookieSessionStorage } from "react-router";
import crypto from "node:crypto";
import { constantTimeCompare } from "~/onboarding/onboarding-request.server";
import { validateCookieConfig } from "../../server-config";

export const CSRF_TOKEN_TTL_MS = 10 * 60 * 1000;
/** Retained as a per-cookie signed quota, never as a process-wide admission quota. */
export const MAX_CSRF_TOKENS_PER_SESSION = 256;
/** Compatibility exports. Stateless issuance does not use global admission state. */
export const MAX_CSRF_TOKENS_GLOBAL = 0;
export const MAX_CSRF_SESSIONS = 0;
export const MAX_CSRF_SOURCES = 0;
export const MAX_CSRF_TOKENS_PER_SOURCE = 0;
const CSRF_TOKEN_BYTES = 32;
const MAX_CONSUMED_TOKENS = 16_384;

type CsrfCookie = {
  csrfToken?: string;
  csrfTokenExpiresAt?: number;
  csrfSessionId?: string;
  csrfIssuanceCount?: number;
};

const csrfSessionStorage = createCookieSessionStorage<CsrfCookie>({
  cookie: {
    name: "__onboarding_csrf",
    httpOnly: true,
    path: "/",
    sameSite: "strict",
    secrets: [process.env.SESSION_KEY || process.env.CSRF_SESSION_KEY || (process.env.NODE_ENV === "production"
      ? (() => { throw new Error("SESSION_KEY is required in production."); })()
      : crypto.randomBytes(64).toString("hex"))],
    secure: validateCookieConfig().secureCookies,
    maxAge: 60 * 60,
  },
});

/**
 * CSRF admission is deliberately stateless. A signed cookie carries the
 * session nonce, current token, expiry, and a bounded per-cookie issuance
 * counter. Cookie-less requests therefore cannot consume a shared pool.
 *
 * The small consumed-token map only closes the race/replay window for a token
 * that was rotated. It is an eviction-only cache: it can never deny issuance,
 * and eviction cannot invalidate a current cookie/token.
 */
const consumedTokens = new Map<string, number>();
const sessionLocks = new Map<string, Promise<void>>();

const newToken = (): string => crypto.randomBytes(CSRF_TOKEN_BYTES).toString("base64url");
const tokenKey = (sessionId: string, token: string): string =>
  crypto.createHash("sha256").update(sessionId).update("\0").update(token).digest("hex");
const nowMs = (): number => Date.now();

const isValidToken = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0 && value.length <= 512 && /^[a-zA-Z0-9_-]+$/.test(value);
const isValidSessionId = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0 && value.length <= 64 && /^[a-zA-Z0-9_-]+$/.test(value);

const cleanupConsumed = (): void => {
  const now = nowMs();
  for (const [key, expiresAt] of consumedTokens) {
    if (expiresAt <= now) consumedTokens.delete(key);
  }
  while (consumedTokens.size > MAX_CONSUMED_TOKENS) {
    const oldest = consumedTokens.keys().next().value as string | undefined;
    if (!oldest) break;
    consumedTokens.delete(oldest);
  }
};

const markConsumed = (key: string, expiresAt: number): void => {
  cleanupConsumed();
  consumedTokens.set(key, expiresAt);
  cleanupConsumed();
};

const normalizeOrigin = (value: string): string => {
  let parsed: URL;
  try { parsed = new URL(value); } catch { throw new Error("Invalid origin configuration."); }
  if ((parsed.protocol !== "http:" && parsed.protocol !== "https:") || parsed.username || parsed.password
    || (parsed.pathname && parsed.pathname !== "/") || parsed.search || parsed.hash) {
    throw new Error("Invalid origin configuration.");
  }
  return parsed.origin;
};

const getConfiguredPublicOrigin = (): string | undefined => {
  const value = process.env.PUBLIC_ORIGIN;
  return value?.trim() ? normalizeOrigin(value.trim()) : undefined;
};

/* Forwarded host/proto are never authoritative inside route-level CSRF checks. */
function assertExpectedOrigin(request: Request): void {
  const requestOrigin = request.headers.get("origin");
  if (!requestOrigin) throw new Error("Missing Origin header.");

  let parsedOrigin: URL;
  try { parsedOrigin = new URL(requestOrigin); } catch { throw new Error("Invalid Origin header."); }
  if ((parsedOrigin.protocol !== "http:" && parsedOrigin.protocol !== "https:") || parsedOrigin.username || parsedOrigin.password) {
    throw new Error("Invalid request origin.");
  }

  const secFetchSite = request.headers.get("sec-fetch-site");
  if (secFetchSite && !["same-origin", "same-site", "none"].includes(secFetchSite.toLowerCase())) {
    throw new Error("Cross-site request blocked.");
  }

  for (const header of ["forwarded", "x-forwarded-host", "x-forwarded-proto", "x-forwarded-port", "x-real-ip"]) {
    if (request.headers.has(header)) throw new Error("Invalid request origin.");
  }

  const requestUrl = new URL(request.url);
  const requestHost = request.headers.get("host");
  if (requestHost && requestHost.trim().toLowerCase() !== requestUrl.host.toLowerCase()) {
    throw new Error("Invalid request origin.");
  }

  const expectedOrigin = getConfiguredPublicOrigin();
  if (expectedOrigin) {
    if (parsedOrigin.origin !== expectedOrigin) throw new Error("Invalid request origin.");
    return;
  }

  if (!isAllowedSetupOrigin(requestUrl.hostname) || !isAllowedSetupOrigin(parsedOrigin.hostname)) {
    throw new Error("Request origin is not trusted for setup.");
  }
  if (parsedOrigin.origin !== requestUrl.origin) throw new Error("Invalid request origin.");
}

export const isAllowedSetupOrigin = (hostname: string): boolean => {
  const lowerHost = hostname.toLowerCase();
  if (lowerHost === "localhost") return true;
  if (isPrivateIPv4(lowerHost)) return true;
  if (lowerHost.includes(":")) return isPrivateIPv6(lowerHost);
  return false;
};

const isPrivateIPv4 = (hostname: string): boolean => {
  const parts = hostname.split(".").map((part) => Number.parseInt(part, 10));
  if (parts.length !== 4 || parts.some((part) => Number.isNaN(part) || part < 0 || part > 255)) return false;
  const [a, b] = parts;
  return a === 127 || a === 10 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b <= 31);
};

export const isPrivateIPv6 = (hostname: string): boolean => {
  hostname = hostname.replace(/^\[|\]$/g, "").toLowerCase();
  if (hostname === "::1") return true;
  const segments = hostname.split(":");
  if (segments.length === 8 && segments.every((segment) => /^[0-9a-f]{1,4}$/.test(segment))) {
    if (segments.slice(0, 7).every((segment) => Number.parseInt(segment, 16) === 0)
      && Number.parseInt(segments[7], 16) === 1) return true;
    const first = Number.parseInt(segments[0], 16);
    if ((first & 0xfe00) === 0xfc00 || (first & 0xffc0) === 0xfe80) return true;
  }
  const firstSegment = segments[0];
  return !!firstSegment && (firstSegment.startsWith("fc") || firstSegment.startsWith("fd")
    || ["fe8", "fe9", "fea", "feb"].includes(firstSegment.slice(0, 3)));
};

export const __resetCsrfStateForTests = (): void => {
  if (process.env.NODE_ENV !== "test") throw new Error("CSRF test hook unavailable outside test environment.");
  consumedTokens.clear();
  sessionLocks.clear();
};

const ensureSessionId = (session: Awaited<ReturnType<typeof csrfSessionStorage.getSession>>): string => {
  const existing = session.get("csrfSessionId");
  if (isValidSessionId(existing)) return existing;
  const generated = newToken();
  session.set("csrfSessionId", generated);
  session.set("csrfIssuanceCount", 0);
  return generated;
};

const withSessionLock = async <T>(sessionId: string, operation: () => Promise<T>): Promise<T> => {
  const previous = sessionLocks.get(sessionId);
  let release!: () => void;
  const current = new Promise<void>((resolve) => { release = resolve; });
  sessionLocks.set(sessionId, current);
  await previous;
  try { return await operation(); }
  finally {
    release();
    if (sessionLocks.get(sessionId) === current) sessionLocks.delete(sessionId);
  }
};

const capacityError = (): Error & { status: number } => Object.assign(
  new Error("CSRF token issuance rate exceeded."), { status: 429 },
);

const usableToken = (session: Awaited<ReturnType<typeof csrfSessionStorage.getSession>>, sessionId: string): string | null => {
  const token = session.get("csrfToken");
  const expiresAt = session.get("csrfTokenExpiresAt");
  if (!isValidToken(token) || typeof expiresAt !== "number" || !Number.isFinite(expiresAt) || expiresAt <= nowMs()) return null;
  cleanupConsumed();
  if (consumedTokens.has(tokenKey(sessionId, token))) return null;
  return token;
};

const issueToken = (session: Awaited<ReturnType<typeof csrfSessionStorage.getSession>>, sessionId: string): { token: string; expiresAt: number } => {
  const count = session.get("csrfIssuanceCount");
  if (typeof count === "number" && Number.isSafeInteger(count) && count >= MAX_CSRF_TOKENS_PER_SESSION) throw capacityError();
  const token = newToken();
  const expiresAt = nowMs() + CSRF_TOKEN_TTL_MS;
  session.set("csrfToken", token);
  session.set("csrfTokenExpiresAt", expiresAt);
  session.set("csrfIssuanceCount", (typeof count === "number" && Number.isSafeInteger(count) ? count : 0) + 1);
  return { token, expiresAt };
};

export async function getCsrfToken(request: Request): Promise<{ token: string; headers?: HeadersInit }> {
  getConfiguredPublicOrigin();
  const fetchSite = request.headers.get("sec-fetch-site");
  if (fetchSite?.toLowerCase() === "cross-site") throw new Error("Cross-site request blocked.");
  if (request.headers.has("origin")) assertExpectedOrigin(request);
  const session = await csrfSessionStorage.getSession(request.headers.get("cookie"));
  const sessionId = ensureSessionId(session);

  return withSessionLock(sessionId, async () => {
    const current = usableToken(session, sessionId);
    if (current) return { token: current };
    const issued = issueToken(session, sessionId);
    return { token: issued.token, headers: { "Set-Cookie": await csrfSessionStorage.commitSession(session) } };
  });
}

export async function validateCsrfToken(request: Request, form: URLSearchParams): Promise<ResponseInit> {
  assertExpectedOrigin(request);
  const formToken = form.get("csrfToken");
  if (!isValidToken(formToken)) throw new Error("Missing CSRF token.");
  const session = await csrfSessionStorage.getSession(request.headers.get("cookie"));
  const sessionId = ensureSessionId(session);

  return withSessionLock(sessionId, async () => {
    const current = usableToken(session, sessionId);
    if (!current || !constantTimeCompare(formToken, current)) throw new Error("Invalid CSRF token.");
    const currentExpiry = session.get("csrfTokenExpiresAt");
    const replacement = issueToken(session, sessionId);
    markConsumed(tokenKey(sessionId, current), typeof currentExpiry === "number" ? currentExpiry : replacement.expiresAt);
    return {
      headers: {
        "X-CSRF-Token": replacement.token,
        "Set-Cookie": await csrfSessionStorage.commitSession(session),
      },
    };
  });
}

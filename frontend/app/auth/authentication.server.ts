import crypto from "node:crypto";
import { existsSync, lstatSync, mkdirSync, readFileSync, renameSync, unlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { IncomingMessage } from "http";
import { backendClient } from "~/clients/backend-client.server";
import { validateCookieConfig } from "../../server-config";

export const IS_FRONTEND_AUTH_DISABLED = process.env.DISABLE_FRONTEND_AUTH === "true";

type User = { username: string };
export type AuthErrorCode = "missing-credentials" | "invalid-credentials" | "backend-unavailable";
type AuthError = Error & { code: AuthErrorCode };
const authError = (code: AuthErrorCode, message: string): AuthError => Object.assign(new Error(message), { code });

const SESSION_COOKIE = "__session";
const SESSION_IDLE_MS = 12 * 60 * 60 * 1000;
const SESSION_MAX_LIFETIME_MS = 7 * 24 * 60 * 60 * 1000;
const SESSION_COOKIE_MAX_AGE = Math.floor(SESSION_MAX_LIFETIME_MS / 1000);
const MAX_SESSIONS = 10_000;
const { secureCookies } = validateCookieConfig();

type ServerSession = { user: User; createdAt: number; lastSeenAt: number; revision: number };
type PersistedSession = Omit<ServerSession, "revision"> & { id: string };
type SessionRevocationListener = () => void;

/**
 * Browser cookies contain only opaque IDs. The bounded registry is persisted
 * with owner-only permissions in the container's private temporary storage so
 * a normal process/container restart does not turn an authenticated browser
 * into an anonymous onboarding request. A recreated container still starts
 * with no sessions, as intended.
 */
const sessions = new Map<string, ServerSession>();
const sessionListeners = new Map<string, Set<SessionRevocationListener>>();
let sessionRevision = 0;
let sessionStoreLoaded = false;
const sessionStoreDirectory = process.env.FRONTEND_SESSION_STORE_DIR || join(
  tmpdir(),
  `nzbdav-frontend-sessions-${crypto.createHash("sha256").update(process.env.SESSION_KEY || "development").digest("hex").slice(0, 32)}`,
);
const sessionStorePath = join(sessionStoreDirectory, "sessions.json");

const validSessionId = (value: string): boolean => /^[A-Za-z0-9_-]{43}$/.test(value);
const validUsername = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0 && value.length <= 255 && !/[\u0000-\u001f\u007f]/.test(value);
const validTimestamp = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value > 0;
const currentTime = (): number => Date.now();

const isPrivateOwnedStoreObject = (stat: { mode: number; uid: number }, mode: number): boolean => {
  if (process.platform === "win32") return true;
  return (stat.mode & 0o777) === mode
    && (typeof process.getuid !== "function" || stat.uid === process.getuid());
};

const ensureSessionStoreDirectory = (): void => {
  mkdirSync(sessionStoreDirectory, { recursive: true, mode: 0o700 });
  const stat = lstatSync(sessionStoreDirectory);
  if (!stat.isDirectory() || stat.isSymbolicLink() || !isPrivateOwnedStoreObject(stat, 0o700)) {
    throw new Error("Frontend session storage is unavailable.");
  }
};

const loadSessions = (): void => {
  if (sessionStoreLoaded || process.env.NODE_ENV === "test") return;
  sessionStoreLoaded = true;
  try {
    ensureSessionStoreDirectory();
    if (!existsSync(sessionStorePath)) return;
    const stat = lstatSync(sessionStorePath);
    if (!stat.isFile() || stat.isSymbolicLink() || !isPrivateOwnedStoreObject(stat, 0o600)
      || stat.size > 4 * 1024 * 1024) throw new Error("invalid session store");
    const value: unknown = JSON.parse(readFileSync(sessionStorePath, "utf8"));
    if (!Array.isArray(value) || value.length > MAX_SESSIONS) throw new Error("invalid session store");
    const now = currentTime();
    for (const item of value) {
      if (!item || typeof item !== "object" || Array.isArray(item)) throw new Error("invalid session store");
      const entry = item as Partial<PersistedSession>;
      if (!validSessionId(entry.id || "") || !validUsername(entry.user?.username)
        || !validTimestamp(entry.createdAt) || !validTimestamp(entry.lastSeenAt)
        || entry.createdAt! > entry.lastSeenAt!
        || now - entry.lastSeenAt! > SESSION_IDLE_MS || now - entry.createdAt! > SESSION_MAX_LIFETIME_MS) continue;
      sessions.set(entry.id!, {
        user: { username: entry.user!.username },
        createdAt: entry.createdAt!,
        lastSeenAt: entry.lastSeenAt!,
        revision: sessionRevision,
      });
    }
  } catch {
    // A malformed or inaccessible registry fails closed rather than accepting
    // a browser cookie without a matching server-side record.
    sessions.clear();
  }
};

const persistSessions = (): void => {
  if (process.env.NODE_ENV === "test") return;
  ensureSessionStoreDirectory();
  const value: PersistedSession[] = [...sessions.entries()].map(([id, session]) => ({
    id,
    user: { username: session.user.username },
    createdAt: session.createdAt,
    lastSeenAt: session.lastSeenAt,
  }));
  const temporary = join(sessionStoreDirectory, `.sessions-${crypto.randomBytes(16).toString("hex")}.tmp`);
  try {
    writeFileSync(temporary, JSON.stringify(value), { encoding: "utf8", mode: 0o600, flag: "wx" });
    renameSync(temporary, sessionStorePath);
  } finally {
    try { if (existsSync(temporary)) unlinkSync(temporary); } catch { /* best-effort temp cleanup */ }
  }
};

const removeSessionById = (id: string): boolean => {
  if (!sessions.delete(id)) return false;
  persistSessions();
  sessionRevision += 1;
  const listeners = sessionListeners.get(id);
  sessionListeners.delete(id);
  if (listeners) {
    for (const listener of listeners) {
      try { listener(); } catch { /* a dead socket must not block other revocations */ }
    }
  }
  return true;
};

const removeExpiredSessions = (now = currentTime()): void => {
  loadSessions();
  let removed = false;
  for (const [id, session] of sessions) {
    if (now - session.lastSeenAt > SESSION_IDLE_MS || now - session.createdAt > SESSION_MAX_LIFETIME_MS) {
      sessions.delete(id);
      const listeners = sessionListeners.get(id);
      sessionListeners.delete(id);
      if (listeners) for (const listener of listeners) {
        try { listener(); } catch { /* expiry must continue */ }
      }
      removed = true;
    }
  }
  if (removed) {
    sessionRevision += 1;
    persistSessions();
  }
};

const cookieValue = (request: Request | IncomingMessage): string | undefined => {
  const header = request instanceof Request ? request.headers.get("cookie") : request.headers.cookie;
  if (!header) return undefined;
  for (const part of header.split(";")) {
    const separator = part.indexOf("=");
    if (separator < 0 || part.slice(0, separator).trim() !== SESSION_COOKIE) continue;
    const value = part.slice(separator + 1).trim();
    return validSessionId(value) ? value : undefined;
  }
  return undefined;
};

const cookieHeader = (value: string, maxAge: number): string => {
  const secure = secureCookies ? "; Secure" : "";
  return `${SESSION_COOKIE}=${value}; Max-Age=${maxAge}; Path=/; HttpOnly; SameSite=Strict${secure}`;
};

const removeSession = (request: Request | IncomingMessage): void => {
  const id = cookieValue(request);
  if (id) removeSessionById(id);
};

const createSession = (user: User): string => {
  removeExpiredSessions();
  if (sessions.size >= MAX_SESSIONS) {
    const oldest = [...sessions.entries()].sort((a, b) => a[1].lastSeenAt - b[1].lastSeenAt)[0]?.[0];
    if (oldest) removeSessionById(oldest);
  }
  const id = crypto.randomBytes(32).toString("base64url");
  const now = currentTime();
  sessions.set(id, { user, createdAt: now, lastSeenAt: now, revision: sessionRevision });
  try {
    persistSessions();
  } catch (error) {
    sessions.delete(id);
    throw error;
  }
  return id;
};

const sessionFor = (request: Request | IncomingMessage): { id: string; session: ServerSession } | undefined => {
  removeExpiredSessions();
  const id = cookieValue(request);
  if (!id) return undefined;
  const session = sessions.get(id);
  if (!session) return undefined;
  session.lastSeenAt = currentTime();
  return { id, session };
};

export async function isAuthenticated(request: Request | IncomingMessage): Promise<boolean> {
  if (IS_FRONTEND_AUTH_DISABLED) return true;
  return !!sessionFor(request);
}

export type AuthenticatedSession = {
  /** Opaque in-memory session identifier; never the raw cookie value in logs. */
  id: string;
  revision: number;
  expiresAt: number;
  subscribe: (listener: SessionRevocationListener) => () => void;
};

/** Return the exact session identity used for a long-lived connection. */
export function getAuthenticatedSession(request: Request | IncomingMessage): AuthenticatedSession | null {
  if (IS_FRONTEND_AUTH_DISABLED) {
    return { id: "frontend-auth-disabled", revision: sessionRevision, expiresAt: Number.POSITIVE_INFINITY, subscribe: () => () => undefined };
  }
  const current = sessionFor(request);
  if (!current) return null;
  const { id, session } = current;
  return {
    id,
    revision: session.revision,
    expiresAt: Math.min(session.createdAt + SESSION_MAX_LIFETIME_MS, session.lastSeenAt + SESSION_IDLE_MS),
    subscribe: (listener) => subscribeToSession(id, session.revision, listener),
  };
}

export function subscribeToSession(
  sessionId: string,
  revision: number,
  listener: SessionRevocationListener,
): () => void {
  const current = sessions.get(sessionId);
  if (!current || current.revision !== revision) return () => undefined;
  let listeners = sessionListeners.get(sessionId);
  if (!listeners) sessionListeners.set(sessionId, listeners = new Set());
  listeners.add(listener);
  return () => {
    listeners?.delete(listener);
    if (listeners?.size === 0) sessionListeners.delete(sessionId);
  };
}

export async function loginWithCredentials(request: Request, username: string, password: string): Promise<ResponseInit> {
  const user = await authenticate(username, password);
  removeSession(request);
  const id = createSession(user);
  return { headers: { "Set-Cookie": cookieHeader(id, SESSION_COOKIE_MAX_AGE) } };
}

export async function logout(request: Request): Promise<ResponseInit> {
  removeSession(request);
  return { headers: { "Set-Cookie": cookieHeader("", 0) } };
}

export async function setSessionUser(request: Request, username: string): Promise<ResponseInit> {
  removeSession(request);
  const id = createSession({ username });
  return { headers: { "Set-Cookie": cookieHeader(id, SESSION_COOKIE_MAX_AGE) } };
}

/** Administrative hook for logout-all/password rotation without a global key change. */
export function revokeAllSessions(): void {
  const listeners = [...sessionListeners.values()].flatMap((value) => [...value]);
  sessions.clear();
  sessionListeners.clear();
  persistSessions();
  sessionRevision += 1;
  for (const listener of listeners) {
    try { listener(); } catch { /* revocation continues for every connection */ }
  }
}

export function getSessionRevision(): number { return sessionRevision; }

/** Test/diagnostic hook; it cannot be used outside test mode. */
export function __resetAuthenticationStateForTests(): void {
  if (process.env.NODE_ENV !== "test") throw new Error("Authentication test hook unavailable outside test environment.");
  const listeners = [...sessionListeners.values()].flatMap((value) => [...value]);
  sessions.clear();
  sessionListeners.clear();
  sessionRevision = 0;
  sessionStoreLoaded = false;
  for (const listener of listeners) {
    try { listener(); } catch { /* test cleanup must not leak a listener */ }
  }
}

export async function authenticate(username: string, password: string): Promise<User> {
  if (!username || !password) throw authError("missing-credentials", "Username and password required.");
  try {
    if (await backendClient.authenticate(username, password)) return { username };
  } catch {
    throw authError("backend-unavailable", "Authentication service unavailable.");
  }
  throw authError("invalid-credentials", "Invalid credentials.");
}

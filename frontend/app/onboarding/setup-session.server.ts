import { createCookieSessionStorage } from "react-router";
import crypto from "node:crypto";
import type { SetupIndexerPayload, SetupUsenetProviderPayload } from "~/clients/backend-client.server";
import { validateCookieConfig } from "../../server-config";

const MAX_ACTIVE_SETUP_SESSIONS = 1;
const HANDLE_TTL_GRACE_SECONDS = 15 * 60;
const MAX_USENET_PROVIDER_COUNT = 32;
const MAX_INDEXER_COUNT = 100;
const MAX_GRANT_BYTES = 512;
const MAX_GRANT_PATTERN = /^[A-Za-z0-9._-]+$/;

const setupSessionStorage = createCookieSessionStorage<{
  setupHandle?: string;
}>({
  cookie: {
    name: "__setup",
    httpOnly: true,
    path: "/",
    sameSite: "strict",
    secrets: [
      process?.env?.SESSION_KEY || process?.env?.SETUP_SESSION_KEY || (process.env.NODE_ENV === "production"
        ? (() => { throw new Error("SESSION_KEY is required in production."); })()
        : crypto.randomBytes(64).toString("hex")),
    ],
    secure: validateCookieConfig().secureCookies,
    maxAge: HANDLE_TTL_GRACE_SECONDS,
  },
});

type SetupDraftProvider = SetupUsenetProviderPayload & { id: string };
type SetupDraftIndexer = SetupIndexerPayload & { id: string };

type SetupDraftIndexerSummaryPayload = SetupDraftIndexer & SetupDraftIndexerSummary;

type StoredSetupSession = {
  grant: string;
  version: number;
  expiresAtUtc: number;
  providers: SetupDraftProvider[];
  indexers: SetupDraftIndexer[];
};

const setupGrantStore = new Map<string, StoredSetupSession>();
const setupExpiryTimers = new Map<string, ReturnType<typeof setTimeout>>();

const wipeSetupSession = (session: StoredSetupSession): void => {
  // Clear every secret-bearing reference before allowing the object to become
  // unreachable. This also protects callers that still hold a draft object.
  session.grant = "";
  for (const provider of session.providers) provider.Pass = "";
  for (const indexer of session.indexers) indexer.ApiKey = "";
  session.providers.length = 0;
  session.indexers.length = 0;
  session.expiresAtUtc = 0;
  session.version = 0;
};

const removeStoredSetupSession = (handle: string, expected?: StoredSetupSession): void => {
  const stored = setupGrantStore.get(handle);
  if (!stored || (expected && stored !== expected)) return;
  const timer = setupExpiryTimers.get(handle);
  if (timer) clearTimeout(timer);
  setupExpiryTimers.delete(handle);
  wipeSetupSession(stored);
  setupGrantStore.delete(handle);
};

const expireSetupSession = async (handle: string, expected: StoredSetupSession): Promise<void> => {
  await withSetupSessionLock(handle, (stored) => {
    if (stored === expected && stored.expiresAtUtc <= Date.now()) removeStoredSetupSession(handle, stored);
  }).catch(() => undefined);
};

const scheduleSetupExpiry = (handle: string, session: StoredSetupSession): void => {
  const timer = setTimeout(() => { void expireSetupSession(handle, session); }, Math.max(0, session.expiresAtUtc - Date.now()));
  // An abandoned setup grant must not keep the frontend process alive.
  if (typeof timer.unref === "function") timer.unref();
  setupExpiryTimers.set(handle, timer);
};

export type SetupDraftSummaryItem = {
  id: string;
  host: string;
  port: number;
  user: string;
  maxConnections: number;
  type: number;
  useSsl: boolean;
};

export type SetupDraftIndexerSummary = {
  id: string;
  name: string;
  url: string;
  allowPrivateNetwork: boolean;
};

export type SetupDraftSummary = {
  hasProviderDraft: boolean;
  hasIndexerDraft: boolean;
  providerCount: number;
  indexerCount: number;
  providers: SetupDraftSummaryItem[];
  indexers: SetupDraftIndexerSummary[];
};

export type SetupSessionState = SetupDraftSummary & {
  hasSetupSession: boolean;
  hasRecoveredSession: boolean;
  expiresAtUtc: string | null;
};

export type SetupGrantLookup = SetupSessionState & {
  grant: string | null;
};

const pruneExpired = (): void => {
  const now = Date.now();
  for (const [handle, value] of setupGrantStore.entries()) {
    if (value.expiresAtUtc <= now) removeStoredSetupSession(handle, value);
  }

  while (setupGrantStore.size > MAX_ACTIVE_SETUP_SESSIONS) {
    const oldest = setupGrantStore.keys().next().value;
    if (!oldest) break;
    removeStoredSetupSession(oldest);
  }
};

const toSummary = (session?: StoredSetupSession): SetupDraftSummary => {
  if (!session) {
    return {
      hasProviderDraft: false,
      hasIndexerDraft: false,
      providerCount: 0,
      indexerCount: 0,
      providers: [],
      indexers: [],
    };
  }

  return {
    hasProviderDraft: session.providers.length > 0,
    hasIndexerDraft: session.indexers.length > 0,
    providerCount: session.providers.length,
    indexerCount: session.indexers.length,
    providers: session.providers.map((provider) => ({
      id: provider.id,
      host: provider.Host,
      port: provider.Port,
      user: provider.User,
      maxConnections: provider.MaxConnections,
      type: provider.Type,
      useSsl: provider.UseSsl,
    })),
    indexers: session.indexers.map((indexer) => ({
      id: indexer.id,
      name: indexer.Name,
      url: indexer.Url,
      allowPrivateNetwork: indexer.AllowPrivateNetwork === true,
    })),
  };
};

const parseExpiry = (expiresAtUtc: string): number => {
  const parsed = Date.parse(expiresAtUtc);
  if (Number.isNaN(parsed)) {
    throw new Error("Invalid setup grant expiry.");
  }

  if (parsed <= Date.now()) {
    throw new Error("Setup grant has expired.");
  }

  return parsed;
};

const isValidGrant = (grant: string): boolean => {
  return grant.length > 0 && grant.length <= MAX_GRANT_BYTES && MAX_GRANT_PATTERN.test(grant);
};

type SetupSessionLock = {
  release: () => void;
  promise: Promise<void>;
};

const setupSessionLocksMap = new Map<string, SetupSessionLock>();

const withSetupSessionLock = async <T>(
  handle: string,
  operation: (session: StoredSetupSession) => Promise<T> | T,
): Promise<T> => {
  const current = setupSessionLocksMap.get(handle);
  let release!: () => void;
  const signal = new Promise<void>((resolve) => {
    release = resolve;
  });
  const lock: SetupSessionLock = {
    release,
    promise: signal,
  };

  setupSessionLocksMap.set(handle, lock);
  await current?.promise;

  try {
    const stored = setupGrantStore.get(handle);
    if (!stored) {
      throw new Error("Setup session expired.");
    }

    if (stored.expiresAtUtc <= Date.now()) {
      removeStoredSetupSession(handle, stored);
      throw new Error("Setup session expired.");
    }

    return await operation(stored);
  } finally {
    lock.release();
    if (setupSessionLocksMap.get(handle) === lock) {
      setupSessionLocksMap.delete(handle);
    }
  }
};

type SetupSessionStateBag = Awaited<ReturnType<typeof setupSessionStorage.getSession>>;

const withSession = async (request: Request): Promise<{ session: SetupSessionStateBag; handle: string | undefined }> => {
  const session = await setupSessionStorage.getSession(request.headers.get("cookie"));
  const setupHandle = session.get("setupHandle");
  return {
    session,
    handle: typeof setupHandle === "string" ? setupHandle : undefined,
  };
};

const disposeSetupSessionStateInternal = (): void => {
  for (const timer of setupExpiryTimers.values()) clearTimeout(timer);
  setupExpiryTimers.clear();
  for (const session of setupGrantStore.values()) wipeSetupSession(session);
  setupGrantStore.clear();
  setupSessionLocksMap.clear();
};

export const __resetSetupSessionStateForTests = (): void => {
  if (process?.env?.NODE_ENV !== "test") {
    throw new Error("Setup session test hook unavailable outside test environment.");
  }
  disposeSetupSessionStateInternal();
};

export const disposeSetupSessionState = (): void => {
  disposeSetupSessionStateInternal();
};

export async function getSetupSessionState(request: Request): Promise<SetupSessionState> {
  const { handle } = await withSession(request);

  if (!handle) {
    return {
      hasSetupSession: false,
      hasRecoveredSession: false,
      ...toSummary(),
      expiresAtUtc: null,
    };
  }

  const stored = await withSetupSessionLock(handle, (sessionState) => {
    if (sessionState.expiresAtUtc <= Date.now()) {
      removeStoredSetupSession(handle, sessionState);
      throw new Error("Setup session expired.");
    }

    return sessionState;
  }).catch((error) => {
    if ((error as Error).message === "Setup session expired.") {
      return null;
    }

    throw error;
  });

  if (!stored) {
    return {
      hasSetupSession: false,
      hasRecoveredSession: true,
      ...toSummary(),
      expiresAtUtc: null,
    };
  }

  return {
    hasSetupSession: true,
    hasRecoveredSession: false,
    ...toSummary(stored),
    expiresAtUtc: new Date(stored.expiresAtUtc).toISOString(),
  };
}

export async function getSetupGrantFromSession(request: Request): Promise<SetupGrantLookup> {
  const { handle } = await withSession(request);

  if (!handle) {
    return {
      hasSetupSession: false,
      hasRecoveredSession: false,
      grant: null,
      ...toSummary(),
      expiresAtUtc: null,
    };
  }

  const stored = await withSetupSessionLock(handle, (sessionState) => {
    if (sessionState.expiresAtUtc <= Date.now()) {
      removeStoredSetupSession(handle, sessionState);
      throw new Error("Setup session expired.");
    }

    return sessionState;
  }).catch((error) => {
    if ((error as Error).message === "Setup session expired.") {
      return null;
    }

    throw error;
  });

  if (!stored) {
    return {
      hasSetupSession: false,
      hasRecoveredSession: true,
      grant: null,
      ...toSummary(),
      expiresAtUtc: null,
    };
  }

  return {
    hasSetupSession: true,
    hasRecoveredSession: false,
    grant: stored.grant,
    ...toSummary(stored),
    expiresAtUtc: new Date(stored.expiresAtUtc).toISOString(),
  };
}

type SetupDraftPayload = {
  hasProviderDraft: boolean;
  hasIndexerDraft: boolean;
  providerCount: number;
  indexerCount: number;
  grant: string | null;
  providers: (SetupUsenetProviderPayload & SetupDraftSummaryItem)[];
  indexers: (SetupIndexerPayload & SetupDraftIndexerSummaryPayload)[];
  version: number;
};

export async function getSetupDraftFromSession(request: Request): Promise<SetupDraftPayload> {
  const { handle } = await withSession(request);

  if (!handle) {
    return {
      hasProviderDraft: false,
      hasIndexerDraft: false,
      providerCount: 0,
      indexerCount: 0,
      grant: null,
      version: 0,
      providers: [],
      indexers: [],
    };
  }

  const stored = await withSetupSessionLock(handle, (sessionState) => {
    if (sessionState.expiresAtUtc <= Date.now()) {
      removeStoredSetupSession(handle, sessionState);
      throw new Error("Setup session expired.");
    }

    return sessionState;
  }).catch((error) => {
    if ((error as Error).message === "Setup session expired.") {
      return null;
    }

    throw error;
  });

  if (!stored) {
    return {
      hasProviderDraft: false,
      hasIndexerDraft: false,
      providerCount: 0,
      indexerCount: 0,
      grant: null,
      version: 0,
      providers: [],
      indexers: [],
    };
  }

  return {
    hasProviderDraft: stored.providers.length > 0,
    hasIndexerDraft: stored.indexers.length > 0,
    providerCount: stored.providers.length,
    indexerCount: stored.indexers.length,
    providers: stored.providers.map((provider) => ({
      id: provider.id,
      Host: provider.Host,
      Port: provider.Port,
      UseSsl: provider.UseSsl,
      User: provider.User,
      Pass: provider.Pass,
      MaxConnections: provider.MaxConnections,
      Type: provider.Type,
      host: provider.Host,
      port: provider.Port,
      user: provider.User,
      maxConnections: provider.MaxConnections,
      type: provider.Type,
      useSsl: provider.UseSsl,
    })),
    indexers: stored.indexers.map((indexer) => ({
      id: indexer.id,
      Name: indexer.Name,
      Url: indexer.Url,
      ApiKey: indexer.ApiKey,
      name: indexer.Name,
      url: indexer.Url,
      // The private-network capability is consumed by configureAndRunSetup;
      // retain its typed form as well as the safe UI summary projection.
      AllowPrivateNetwork: indexer.AllowPrivateNetwork === true,
      allowPrivateNetwork: indexer.AllowPrivateNetwork === true,
    })),
    grant: stored.grant,
    version: stored.version,
  };
}

export async function createSetupSession(request: Request, grant: string, expiresAtUtc: string): Promise<ResponseInit> {
  if (!isValidGrant(grant)) {
    throw new Error("Invalid setup grant.");
  }

  // Validate backend expiry before replacing the current session. A rejected handoff must
  // never destroy a still-live grant.
  const expiresAt = parseExpiry(expiresAtUtc);
  const { session, handle } = await withSession(request);
  if (typeof handle === "string") {
    removeStoredSetupSession(handle);
  }

  pruneExpired();
  const setupHandle = crypto.randomBytes(32).toString("base64url");

  const storedSession: StoredSetupSession = {
    grant,
    version: 0,
    expiresAtUtc: expiresAt,
    providers: [],
    indexers: [],
  };
  setupGrantStore.set(setupHandle, storedSession);
  scheduleSetupExpiry(setupHandle, storedSession);

  while (setupGrantStore.size > MAX_ACTIVE_SETUP_SESSIONS) {
    const oldest = setupGrantStore.keys().next().value;
    if (!oldest) break;
    removeStoredSetupSession(oldest);
  }

  session.set("setupHandle", setupHandle);
  return {
    headers: {
      "Set-Cookie": await setupSessionStorage.commitSession(session),
    },
  };
}

async function mutateDraft(
  request: Request,
  mutate: (session: StoredSetupSession) => void,
): Promise<void> {
  const { handle } = await withSession(request);
  if (!handle) {
    throw new Error("Missing setup session.");
  }

  await withSetupSessionLock(handle, (stored) => {
    mutate(stored);
    stored.version += 1;
  });
}

export async function addSetupProvider(request: Request, provider: SetupUsenetProviderPayload): Promise<string> {
  if (provider.Host.length > 255 || provider.User.length > 128 || provider.Pass.length > 256 || provider.Port < 1 || provider.Port > 65535) {
    throw new Error("Invalid provider payload.");
  }

  if (provider.Pass.length === 0) {
    throw new Error("Provider password is required.");
  }

  let providerId = "";
  await mutateDraft(request, (session) => {
    if (session.providers.length >= MAX_USENET_PROVIDER_COUNT) {
      throw new Error("Too many usenet providers.");
    }

    providerId = crypto.randomBytes(16).toString("base64url");
    session.providers.push({ ...provider, id: providerId });
  });

  return providerId;
}

export async function addSetupIndexer(request: Request, indexer: SetupIndexerPayload): Promise<string> {
  if (indexer.Name.length > 128 || indexer.ApiKey.length > 512 || indexer.Url.length === 0) {
    throw new Error("Invalid indexer payload.");
  }

  if (indexer.ApiKey.length === 0) {
    throw new Error("Indexer API key is required.");
  }

  let indexerId = "";
  await mutateDraft(request, (session) => {
    if (session.indexers.length >= MAX_INDEXER_COUNT) {
      throw new Error("Too many indexers.");
    }

    indexerId = crypto.randomBytes(16).toString("base64url");
    session.indexers.push({ ...indexer, id: indexerId });
  });

  return indexerId;
}

export async function removeSetupProvider(request: Request, providerId: string): Promise<void> {
  await mutateDraft(request, (session) => {
    const count = session.providers.length;
    session.providers = session.providers.filter((entry) => entry.id !== providerId);
    if (session.providers.length === count) {
      throw new Error("Unknown provider.");
    }
  });
}

export async function removeSetupIndexer(request: Request, indexerId: string): Promise<void> {
  await mutateDraft(request, (session) => {
    const count = session.indexers.length;
    session.indexers = session.indexers.filter((entry) => entry.id !== indexerId);
    if (session.indexers.length === count) {
      throw new Error("Unknown indexer.");
    }
  });
}

export async function clearSetupSession(request: Request): Promise<ResponseInit> {
  const { session, handle } = await withSession(request);
  if (typeof handle === "string") {
    removeStoredSetupSession(handle);
    setupSessionLocksMap.delete(handle);
    session.unset("setupHandle");
  }

  return {
    headers: {
      "Set-Cookie": await setupSessionStorage.destroySession(session),
    },
  };
}

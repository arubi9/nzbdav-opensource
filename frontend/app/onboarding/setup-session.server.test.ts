import { describe, expect, it, beforeEach, vi } from "vitest";
import {
  __resetSetupSessionStateForTests,
  addSetupIndexer,
  addSetupProvider,
  clearSetupSession,
  createSetupSession,
  getSetupDraftFromSession,
  getSetupSessionState,
  getSetupGrantFromSession,
  removeSetupIndexer,
  removeSetupProvider,
} from "./setup-session.server";

const createCookieRequest = (cookie?: string): Request => {
  return new Request("http://localhost/onboarding", {
    method: "GET",
    headers: cookie ? { cookie } : undefined,
  });
};

const getSetupCookie = (headers?: HeadersInit): string => {
  const merged = new Headers(headers || {});
  return merged.get("Set-Cookie") ?? merged.get("set-cookie") ?? "";
};

const futureIso = (): string => {
  const date = new Date(Date.now() + 60_000);
  return date.toISOString();
};

beforeEach(() => {
  __resetSetupSessionStateForTests();
});

describe("setup session storage", () => {
  it("validates setup grant format before creating a session", async () => {
    await expect(createSetupSession(createCookieRequest(), "bad token?", futureIso())).rejects.toThrow("Invalid setup grant.");
  });

  it("rejects expired grants when creating a session", async () => {
    const expired = new Date(Date.now() - 60_000).toISOString();
    await expect(createSetupSession(createCookieRequest(), "valid-grant-token", expired)).rejects.toThrow("Setup grant has expired.");
  });

  it("does not replace a live session when a new backend expiry is rejected", async () => {
    const request = createCookieRequest();
    const created = await createSetupSession(request, "live-grant", futureIso());
    const cookie = getSetupCookie(created.headers);
    const expired = new Date(Date.now() - 60_000).toISOString();

    await expect(createSetupSession(createCookieRequest(cookie), "replacement", expired)).rejects.toThrow("Setup grant has expired.");
    await expect(getSetupGrantFromSession(createCookieRequest(cookie))).resolves.toMatchObject({ grant: "live-grant", hasSetupSession: true });
  });

  it("replaces prior active setup session when creating a new one", async () => {
    const request = createCookieRequest();
    const firstSession = await createSetupSession(request, "valid-grant-1", futureIso());
    const firstCookie = getSetupCookie(firstSession.headers);

    const firstState = await getSetupSessionState(createCookieRequest(firstCookie));
    expect(firstState.hasSetupSession).toBe(true);
    expect(firstState.hasRecoveredSession).toBe(false);

    const secondSession = await createSetupSession(createCookieRequest(firstCookie), "valid-grant-2", futureIso());
    const secondCookie = getSetupCookie(secondSession.headers);

    const secondState = await getSetupSessionState(createCookieRequest(secondCookie));
    expect(secondState.hasSetupSession).toBe(true);

    const staleState = await getSetupSessionState(createCookieRequest(firstCookie));
    expect(staleState.hasSetupSession).toBe(false);
    expect(staleState.hasRecoveredSession).toBe(true);

    const secondGrant = await getSetupGrantFromSession(createCookieRequest(secondCookie));
    expect(secondGrant.grant).toBe("valid-grant-2");
  });

  it("actively expires an abandoned session without a follow-up request", async () => {
    vi.useFakeTimers();
    try {
      const created = await createSetupSession(createCookieRequest(), "timer-grant", futureIso());
      const cookie = getSetupCookie(created.headers);
      vi.advanceTimersByTime(60_001);
      await vi.runAllTimersAsync();
      await expect(getSetupSessionState(createCookieRequest(cookie))).resolves.toMatchObject({
        hasSetupSession: false,
        hasRecoveredSession: true,
      });
    } finally {
      vi.useRealTimers();
    }
  });

  it("tracks session version through mutations while preserving secrets", async () => {
    const request = createCookieRequest();
    const sessionCreate = await createSetupSession(request, "valid-grant-3", futureIso());
    const cookie = getSetupCookie(sessionCreate.headers);
    const sessionRequest = createCookieRequest(cookie);

    await Promise.all([
      addSetupProvider(sessionRequest, {
        Host: "p1.example",
        Port: 563,
        UseSsl: true,
        User: "u1",
        Pass: "pass-1",
        MaxConnections: 1,
        Type: 1,
      }),
      addSetupProvider(sessionRequest, {
        Host: "p2.example",
        Port: 564,
        UseSsl: false,
        User: "u2",
        Pass: "pass-2",
        MaxConnections: 2,
        Type: 1,
      }),
      addSetupIndexer(sessionRequest, {
        Name: "idx-1",
        Url: "https://idx.one",
        ApiKey: "key-1",
        AllowPrivateNetwork: true,
      }),
    ]);

    const summary = await getSetupSessionState(sessionRequest);
    expect(JSON.stringify(summary)).not.toContain("pass-1");
    expect(JSON.stringify(summary)).not.toContain("key-1");
    expect(JSON.stringify(summary)).not.toContain("valid-grant-3");

    const draft = await getSetupDraftFromSession(sessionRequest);
    expect(draft.providerCount).toBe(2);
    expect(draft.indexerCount).toBe(1);
    expect(draft.version).toBe(3);
    const providerOne = draft.providers.find((provider) => provider.Host === "p1.example");
    const providerTwo = draft.providers.find((provider) => provider.Host === "p2.example");
    expect(providerOne).toMatchObject({ Host: "p1.example", Pass: "pass-1" });
    expect(providerTwo).toMatchObject({ Host: "p2.example", Pass: "pass-2" });
    expect(draft.indexers[0].ApiKey).toBe("key-1");
    // Configure must retain this flag for private Compose-network indexers.
    expect(draft.indexers[0].AllowPrivateNetwork).toBe(true);

    if (!providerOne) throw new Error("Provider p1.example was not stored.");
    await removeSetupProvider(sessionRequest, providerOne.id);
    await removeSetupIndexer(sessionRequest, draft.indexers[0].id);

    const withRemovals = await getSetupDraftFromSession(sessionRequest);
    expect(withRemovals.version).toBe(5);
    expect(withRemovals.providerCount).toBe(1);
    expect(withRemovals.indexerCount).toBe(0);
    expect(withRemovals.providers).not.toContainEqual(expect.objectContaining({ Host: "p1.example" }));
    expect(withRemovals.providers).toContainEqual(expect.objectContaining({ Host: "p2.example", Pass: "pass-2" }));

    const clear = await clearSetupSession(sessionRequest);
    expect(clear.headers).toBeTruthy();
  });
});

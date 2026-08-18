import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { action, headers as onboardingHeaders, loader } from "./route";
import { backendClient } from "~/clients/backend-client.server";
import { getCsrfToken } from "~/onboarding/onboarding-csrf.server";
import {
  addSetupIndexer,
  addSetupProvider,
  createSetupSession,
  getSetupSessionState,
  __resetSetupSessionStateForTests,
} from "~/onboarding/setup-session.server";
import { setSessionUser } from "~/auth/authentication.server";
import type { Route } from "./+types/route";

const futureIso = (): string => {
  return new Date(Date.now() + 60_000).toISOString();
};

const getCookies = (headers?: HeadersInit): string[] => {
  const merged = new Headers(headers || {});

  if (typeof (merged as Headers).getSetCookie === "function") {
    return (merged as Headers).getSetCookie();
  }

  const cookie = merged.get("Set-Cookie") || merged.get("set-cookie") || "";
  return cookie ? [cookie] : [];
};

const asCookieHeader = (cookies: string[]): string => cookies.join("; ");

const createOnboardingActionRequest = (form: URLSearchParams, cookie: string): Route.ActionArgs["request"] => {
  const body = form.toString();

  return new Request("http://localhost/onboarding", {
    method: "POST",
    headers: {
      origin: "http://localhost",
      "content-type": "application/x-www-form-urlencoded",
      "content-length": String(Buffer.byteLength(body)),
      cookie,
    },
    body,
  }) as Route.ActionArgs["request"];
};

const createOnboardingLoaderRequest = (url: string, cookies: string[]) =>
  new Request(url, {
    method: "GET",
    headers: {
      cookie: asCookieHeader(cookies),
      origin: "http://localhost",
    },
  });

describe("onboarding actions", () => {
  beforeEach(() => {
    process.env.FRONTEND_BACKEND_API_KEY = "api-key";
    __resetSetupSessionStateForTests();
    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({ enabled: true, completed: false, steps: [], services: [], revocationPending: false, repairRequired: false } as any);
    vi.spyOn(backendClient, "isOnboarding").mockResolvedValue(false);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it("preserves every Set-Cookie header when action and loader headers are composed", () => {
    const loaderHeaders = new Headers({ "Set-Cookie": "__onboarding_csrf=loader; Path=/" });
    const actionHeaders = new Headers();
    actionHeaders.append("Set-Cookie", "__setup=handle; Path=/; HttpOnly");
    actionHeaders.append("Set-Cookie", "__session=session; Path=/; HttpOnly");
    actionHeaders.append("Set-Cookie", "__onboarding_csrf=rotated; Path=/; HttpOnly");

    const merged = onboardingHeaders({ loaderHeaders, actionHeaders, errorHeaders: new Headers() } as any);
    // Action rotation supersedes the loader's same-name CSRF cookie, while
    // the independent setup and authenticated-session cookies are retained.
    expect(getCookies(merged).map((cookie) => cookie.split("=", 1)[0]).sort()).toEqual([
      "__onboarding_csrf", "__session", "__setup",
    ]);
  });

  it("renders services by default and allows explicit jellyfin step without session", async () => {
    const servicesResponse = (await loader({ request: new Request("http://localhost/onboarding", {
      method: "GET",
      headers: { origin: "http://localhost" },
    }), params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;

    const servicesData = (await servicesResponse.json()) as any;
    expect(servicesData.mode).toBe("fullstack");
    expect(servicesData.activeStep).toBe("services");

    const jellyfinResponse = (await loader({ request: new Request("http://localhost/onboarding?step=jellyfin", {
      method: "GET",
      headers: { origin: "http://localhost" },
    }), params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;

    const jellyfinData = (await jellyfinResponse.json()) as any;
    expect(jellyfinData.mode).toBe("fullstack");
    expect(jellyfinData.activeStep).toBe("jellyfin");

    expect(jellyfinData.mode).toBe("fullstack");
    expect(jellyfinData.activeStep).toBe("jellyfin");
  });

  it("publishes a same-origin no-store CSRF header for the first public handoff", async () => {
    const response = (await loader({
      request: new Request("http://localhost/onboarding", {
        method: "GET",
        headers: { origin: "http://localhost" },
      }),
      params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;

    const payload = await response.json() as { csrfToken?: string };
    const token = response.headers.get("X-CSRF-Token");
    expect(response.status).toBe(200);
    expect(response.headers.get("Cache-Control")).toContain("no-store");
    expect(token).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(token).toBe(payload.csrfToken);
    expect(response.headers.get("Access-Control-Allow-Origin")).toBeNull();
    expect(getCookies(response.headers)).toEqual(expect.arrayContaining([expect.stringContaining("__onboarding_csrf=")]));
  });

  it("routes completed repair state to repair and completed healthy state to ready", async () => {
    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({
      enabled: true, completed: true, repairRequired: true, steps: [], services: [], revocationPending: false,
    } as any);

    const repairResponse = (await loader({ request: new Request("http://localhost/onboarding?step=ready"), params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;
    expect((await repairResponse.json() as any).activeStep).toBe("repair");

    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({
      enabled: true, completed: true, repairRequired: false, steps: [], services: [
        { name: "nzbdav", ready: true }, { name: "jellyfin", ready: true },
        { name: "sonarr", ready: true }, { name: "radarr", ready: true },
        { name: "prowlarr", ready: true },
      ], revocationPending: false,
    } as any);
    const readyResponse = (await loader({ request: new Request("http://localhost/onboarding?step=repair"), params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;
    expect((await readyResponse.json() as any).activeStep).toBe("ready");
  });

  it("renders legacy redirect contracts", async () => {
    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({ enabled: false, completed: false, steps: [], services: [], revocationPending: false, repairRequired: false } as any);
    vi.spyOn(backendClient, "isOnboarding").mockResolvedValue(false);

    const response = (await loader({ request: new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }), params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;
    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/login?returnTo=%2Fonboarding");
  });

  it("redirects legacy-complete users to home even when setup status is disabled", async () => {
    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({ enabled: false, completed: true, steps: [], services: [], revocationPending: false, repairRequired: false } as any);
    vi.spyOn(backendClient, "isOnboarding").mockResolvedValue(false);

    const sessionCookie = await setSessionUser(new Request("http://localhost/login", { method: "GET", headers: { origin: "http://localhost" } }), "admin").then((s) => asCookieHeader(getCookies(s.headers)));
    const requestWithSession = new Request("http://localhost/onboarding", {
      method: "GET",
      headers: {
        cookie: sessionCookie,
        origin: "http://localhost",
      },
    });

    const response = (await loader({ request: requestWithSession, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;
    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/");
  });

  it("requires authentication when loading the wizard after handoff", async () => {
    const setup = await createSetupSession(
      new Request("http://localhost/onboarding", { method: "GET" }),
      "loader-grant",
      futureIso(),
    );
    const response = (await loader({
      request: createOnboardingLoaderRequest("http://localhost/onboarding?step=usenet", getCookies(setup.headers)),
      params: {},
      context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/login?returnTo=%2Fonboarding");
  });

  it("POSTing handoff creates an active setup session and advances to providers", async () => {
    const issueSpy = vi.spyOn(backendClient, "issueSetupGrant").mockResolvedValue({ grant: "new-grant", expiresAtUtc: futureIso() } as any);

    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const payload = new URLSearchParams({
      action: "handoff",
      csrfToken: csrf.token,
      username: "alice",
      password: "pass",
    });
    const request = createOnboardingActionRequest(payload, asCookieHeader(getCookies(csrf.headers)));
    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/onboarding?step=usenet");
    expect(issueSpy).toHaveBeenCalledOnce();

    const afterCookies = getCookies(response.headers);
    const nextLoad = (await loader({
      request: createOnboardingLoaderRequest("http://localhost/onboarding?step=usenet", afterCookies),
      params: {},
      context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;

    const nextData = (await nextLoad.json()) as any;
    expect(nextData.activeStep).toBe("usenet");
    expect(nextData.setupSessionState.hasSetupSession).toBe(true);
  });

  it("retries setup sessions whose map entry was cleared and keeps evidence needed to renew", async () => {
    const renewSpy = vi.spyOn(backendClient, "renewSetupGrant").mockResolvedValue({ grant: "renew-grant", expiresAtUtc: futureIso() } as any);
    const issueSpy = vi.spyOn(backendClient, "issueSetupGrant").mockResolvedValue({ grant: "should-not-use", expiresAtUtc: futureIso() } as any);

    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }), "valid-grant-provider", futureIso());
    const setupCookies = getCookies(setup.headers);

    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const csrfCookie = getCookies(csrf.headers);

    __resetSetupSessionStateForTests();
    const auth = await setSessionUser(new Request("http://localhost/login"), "admin");

    const recovered = (await loader({
      request: createOnboardingLoaderRequest("http://localhost/onboarding?step=jellyfin", [...setupCookies, ...csrfCookie, ...getCookies(auth.headers)]),
      params: {},
      context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;

    const recoveredData = (await recovered.json()) as any;
    expect(recoveredData.mode).toBe("fullstack");
    expect(recoveredData.activeStep).toBe("jellyfin");

    const payload = new URLSearchParams({
      action: "handoff",
      csrfToken: csrf.token,
      username: "alice",
      password: "pass",
    });

    const handoffRequest = createOnboardingActionRequest(payload, asCookieHeader([...setupCookies, ...csrfCookie]));
    const handoffResponse = (await action({ request: handoffRequest, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;

    expect(handoffResponse.status).toBe(302);
    expect(handoffResponse.headers.get("Location")).toBe("/onboarding?step=usenet");
    expect(renewSpy).toHaveBeenCalledOnce();
    expect(issueSpy).not.toHaveBeenCalled();

    const handoffCookies = getCookies(handoffResponse.headers);
    const postState = await getSetupSessionState(new Request("http://localhost/onboarding", {
      headers: { cookie: asCookieHeader(handoffCookies) },
    }));
    expect(postState.hasSetupSession).toBe(true);
  });

  it("retries expired setup sessions through renew path", async () => {
    vi.useFakeTimers();
    const now = Date.now();
    vi.setSystemTime(new Date(now));

    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }), "valid-grant-expired", futureIso());
    const setupCookies = getCookies(setup.headers);

    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const csrfCookie = getCookies(csrf.headers);

    vi.setSystemTime(new Date(now + 120_000));

    const renewSpy = vi.spyOn(backendClient, "renewSetupGrant").mockResolvedValue({ grant: "renewed-grant", expiresAtUtc: futureIso() } as any);
    const payload = new URLSearchParams({
      action: "handoff",
      csrfToken: csrf.token,
      username: "alice",
      password: "pass",
    });

    const handoffResponse = (await action({
      request: createOnboardingActionRequest(payload, asCookieHeader([...setupCookies, ...csrfCookie])),
      params: {},
      context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;

    expect(handoffResponse.status).toBe(302);
    expect(handoffResponse.headers.get("Location")).toBe("/onboarding?step=usenet");
    expect(renewSpy).toHaveBeenCalledOnce();
  });

  it("adds provider and advances usenet step", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }), "valid-grant-provider", futureIso());

    const payload = new URLSearchParams({
      action: "add-provider",
      csrfToken: csrf.token,
      "provider-host": "provider.local",
      "provider-port": "563",
      "provider-user": "user",
      "provider-pass": "pass",
      "provider-max": "2",
      "provider-ssl": "on",
      "provider-type": "1",
    });

    const auth = await setSessionUser(new Request("http://localhost/login"), "admin");
    const request = createOnboardingActionRequest(payload, asCookieHeader([
      ...getCookies(csrf.headers),
      ...getCookies(setup.headers),
      ...getCookies(auth.headers),
    ]));
    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" }) as Response);

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/onboarding?step=usenet");
  });

  it("adds indexer and advances indexer step", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }), "valid-grant-indexer", futureIso());

    const payload = new URLSearchParams({
      action: "add-indexer",
      csrfToken: csrf.token,
      "indexer-name": "my-indexer",
      "indexer-url": "https://indexer.local",
      "indexer-apikey": "key",
    });

    const auth = await setSessionUser(new Request("http://localhost/login"), "admin");
    const request = createOnboardingActionRequest(payload, asCookieHeader([
      ...getCookies(csrf.headers),
      ...getCookies(setup.headers),
      ...getCookies(auth.headers),
    ]));
    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" }) as Response);

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/onboarding?step=indexers");
  });

  it("keeps configure drafts and session on a retryable setup lease conflict", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET" }), "valid-grant-busy", futureIso());
    const setupCookie = asCookieHeader(getCookies(setup.headers));
    const draftRequest = new Request("http://localhost/onboarding", { headers: { cookie: setupCookie } });
    await addSetupProvider(draftRequest, {
      Host: "provider.local", Port: 563, UseSsl: true, User: "user", Pass: "pass", MaxConnections: 2, Type: 1,
    });
    await addSetupIndexer(draftRequest, {
      Name: "indexer", Url: "https://indexer.local", ApiKey: "key", AllowPrivateNetwork: false,
    });
    const auth = await setSessionUser(new Request("http://localhost/login"), "admin");
    const configureSpy = vi.spyOn(backendClient, "configureAndRunSetup").mockRejectedValue({
      status: 409, code: "conflict", retryAfter: "2",
    });

    const response = await action({
      request: createOnboardingActionRequest(new URLSearchParams({ action: "configure", csrfToken: csrf.token }), asCookieHeader([
        ...getCookies(csrf.headers), ...getCookies(setup.headers), ...getCookies(auth.headers),
      ])),
      params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    }) as Response;

    expect(response.status).toBe(200);
    expect(response.headers.get("Location")).toBeNull();
    expect(response.headers.get("Retry-After")).toBe("2");
    await expect(response.json()).resolves.toEqual(expect.objectContaining({
      mode: "fullstack", step: "configure", retryAfterSeconds: 2, error: "Setup is busy. Retry in 2 seconds.",
    }));
    expect(configureSpy).toHaveBeenCalledOnce();

    const retained = await getSetupSessionState(new Request("http://localhost/onboarding", { headers: { cookie: setupCookie } }));
    expect(retained.hasSetupSession).toBe(true);
    expect(retained.providerCount).toBe(1);
    expect(retained.indexerCount).toBe(1);
  });

  it("requires authentication before reading an existing setup draft", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));

    const payload = new URLSearchParams({
      action: "add-indexer",
      csrfToken: csrf.token,
      "indexer-name": "my-indexer",
      "indexer-url": "https://indexer.local",
      "indexer-apikey": "key",
    });

    const request = createOnboardingActionRequest(payload, asCookieHeader(getCookies(csrf.headers)));
    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/login?returnTo=%2Fonboarding");
  });

  it("blocks provider and indexer mutations after completion when repair is required", async () => {
    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({
      enabled: true,
      completed: true,
      repairRequired: true,
      steps: [],
      services: [],
      revocationPending: false,
    } as any);
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET" }), "completed-grant", futureIso());
    const auth = await setSessionUser(new Request("http://localhost/login"), "operator");
    const secret = "must-not-appear-in-a-repair-error";
    const payload = new URLSearchParams({
      action: "add-provider",
      csrfToken: csrf.token,
      "provider-host": "provider.local",
      "provider-port": "563",
      "provider-user": "operator",
      "provider-pass": secret,
      "provider-max": "2",
      "provider-ssl": "on",
      "provider-type": "1",
    });

    const response = (await action({
      request: createOnboardingActionRequest(payload, asCookieHeader([
        ...getCookies(csrf.headers), ...getCookies(setup.headers), ...getCookies(auth.headers),
      ])),
      params: {},
      context: { VALUE_FROM_EXPRESS: "test" },
      url: new URL("http://localhost/onboarding"),
      pattern: "/onboarding",
    })) as Response;
    const body = await response.text();

    expect(response.status).toBe(200);
    expect(body).toContain("Re-authenticate");
    expect(body).not.toContain(secret);
  });

  it("keeps failed repair retries on repair and does not retain credentials", async () => {
    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({
      enabled: true, completed: true, repairRequired: true, steps: [], services: [], revocationPending: false,
    } as any);
    vi.spyOn(backendClient, "retrySetup").mockResolvedValue({
      enabled: true, completed: true, repairRequired: true, steps: [], services: [], revocationPending: false,
    } as any);
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET" }), "repair-grant", futureIso());
    const auth = await setSessionUser(new Request("http://localhost/login"), "operator");
    const secret = "repair-password-must-not-be-retained";
    const response = (await action({
      request: createOnboardingActionRequest(new URLSearchParams({ action: "repair", csrfToken: csrf.token, password: secret }), asCookieHeader([
        ...getCookies(csrf.headers), ...getCookies(setup.headers), ...getCookies(auth.headers),
      ])),
      params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;
    const body = await response.text();
    expect(response.status).toBe(200);
    expect(body).toContain("repair");
    expect(body).not.toContain(secret);
  });

  it("honors a pending cleanup flag from handoff instead of advancing into setup", async () => {
    vi.spyOn(backendClient, "issueSetupGrant").mockResolvedValue({
      grant: "pending-grant",
      expiresAtUtc: futureIso(),
      revocationPending: true,
    } as any);

    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const payload = new URLSearchParams({ action: "handoff", csrfToken: csrf.token, username: "alice", password: "pass" });
    const response = (await action({
      request: createOnboardingActionRequest(payload, asCookieHeader(getCookies(csrf.headers))),
      params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/onboarding?step=services");
  });

  it("recovers pending cleanup and rotates away the stale local setup handle", async () => {
    const recoverySpy = vi.spyOn(backendClient, "recoverSetupGrant").mockResolvedValue({ revocationPending: false });
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET" }), "stale-grant", futureIso());
    const auth = await setSessionUser(new Request("http://localhost/login"), "admin");
    const payload = new URLSearchParams({
      action: "recover-revocation",
      csrfToken: csrf.token,
      username: "alice",
      password: "pass",
    });

    const response = (await action({
      request: createOnboardingActionRequest(payload, asCookieHeader([
        ...getCookies(csrf.headers), ...getCookies(setup.headers), ...getCookies(auth.headers),
      ])),
      params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    })) as Response;

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/onboarding?step=jellyfin");
    expect(recoverySpy).toHaveBeenCalledWith("alice", "pass", expect.objectContaining({ signal: expect.any(AbortSignal) }));
    const state = await getSetupSessionState(new Request("http://localhost/onboarding", {
      headers: { cookie: asCookieHeader(getCookies(response.headers)) },
    }));
    expect(state.hasSetupSession).toBe(false);
  });

  it("verifies completed services only through an authenticated CSRF POST and discovers repair", async () => {
    vi.spyOn(backendClient, "getSetupStatus").mockResolvedValue({
      enabled: true, completed: true, repairRequired: false, steps: [], services: [
        { name: "nzbdav", ready: false, reason: "setup-run-busy" },
      ], revocationPending: false,
    } as any);
    const verifySpy = vi.spyOn(backendClient, "verifySetupStatus").mockResolvedValue({
      enabled: true, completed: true, repairRequired: true, steps: [], services: [
        { name: "sonarr", ready: false, reason: "sonarr-failed" },
      ], revocationPending: false,
    } as any);
    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const auth = await setSessionUser(new Request("http://localhost/login"), "admin");
    const response = await action({
      request: createOnboardingActionRequest(new URLSearchParams({ action: "verify-services", csrfToken: csrf.token }), asCookieHeader([
        ...getCookies(csrf.headers), ...getCookies(auth.headers),
      ])),
      params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding",
    }) as Response;

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual(expect.objectContaining({ step: "repair" }));
    expect(verifySpy).toHaveBeenCalledWith(expect.objectContaining({ signal: expect.any(AbortSignal) }));
  });

  it("prefers renew on recovered sessions and falls back only when backend reports fresh setup", async () => {
    vi.spyOn(backendClient, "renewSetupGrant").mockRejectedValue({ status: 404 } as { status?: number });
    const issueSpy = vi.spyOn(backendClient, "issueSetupGrant").mockResolvedValue({ grant: "new-grant", expiresAtUtc: futureIso() } as any);

    const csrf = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET", headers: { origin: "http://localhost" } }), "valid-grant-provider", futureIso());

    const payload = new URLSearchParams({
      action: "handoff",
      csrfToken: csrf.token,
      username: "alice",
      password: "pass",
    });

    const request = createOnboardingActionRequest(payload, asCookieHeader([...getCookies(csrf.headers), ...getCookies(setup.headers)]));
    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/onboarding"), pattern: "/onboarding" })) as Response;

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/onboarding?step=usenet");
    expect(issueSpy).toHaveBeenCalledWith("alice", "pass", expect.objectContaining({ signal: expect.any(AbortSignal) }));
  });
});

import { describe, expect, it, beforeEach, vi } from "vitest";
import {
  __resetCsrfStateForTests,
  getCsrfToken,
  validateCsrfToken,
} from "./onboarding-csrf.server";

const ORIGIN = "http://localhost";

const getCookies = (responseHeaders?: HeadersInit): string[] => {
  const headers = new Headers(responseHeaders || {});
  if (typeof (headers as Headers).getSetCookie === "function") {
    return (headers as Headers).getSetCookie();
  }

  const header = headers.get("set-cookie") || headers.get("Set-Cookie");
  return header ? [header] : [];
};

const joinCookies = (cookies: string[]): string => cookies.join("; ");

const createCsrfActionRequest = (
  csrfToken: string,
  cookie?: string,
  origin = ORIGIN,
  requestUrl = "http://localhost/onboarding",
): Request => {
  const form = new URLSearchParams({ csrfToken });
  return new Request(requestUrl, {
    method: "POST",
    headers: {
      origin,
      "content-type": "application/x-www-form-urlencoded",
      cookie: cookie || "",
    },
    body: form,
  });
};

const createSeed = async (request = new Request("http://localhost/onboarding", { method: "GET", headers: { origin: ORIGIN } })) => {
  const response = await getCsrfToken(request);
  return {
    token: response.token,
    cookies: getCookies(response.headers),
  };
};

describe("onboarding CSRF guard", () => {
  beforeEach(() => {
    __resetCsrfStateForTests();
    delete process.env.PUBLIC_ORIGIN;
  });

  it("requires a matching Origin header", async () => {
    const { token, cookies } = await createSeed();

    const request = new Request("http://localhost/onboarding", {
      method: "POST",
      headers: {
        "content-type": "application/x-www-form-urlencoded",
        cookie: joinCookies(cookies),
      },
      body: new URLSearchParams({ csrfToken: token }),
    });

    await expect(validateCsrfToken(request, new URLSearchParams({ csrfToken: token }))).rejects.toThrow("Missing Origin header.");
  });

  it("enforces PUBLIC_ORIGIN exact match", async () => {
    process.env.PUBLIC_ORIGIN = "https://example.com:8443";

    const { token, cookies } = await createSeed(
      new Request("https://example.com:8443/onboarding", { method: "GET", headers: { origin: "https://example.com:8443" } }),
    );

    const request = createCsrfActionRequest(token, joinCookies(cookies), "https://example.com:9000");
    await expect(validateCsrfToken(request, new URLSearchParams({ csrfToken: token }))).rejects.toThrow("Invalid request origin.");

    const goodRequest = createCsrfActionRequest(token, joinCookies(cookies), "https://example.com:8443");
    await expect(validateCsrfToken(goodRequest, new URLSearchParams({ csrfToken: token }))).resolves.toMatchObject({ headers: expect.any(Object) });
  });

  it("rejects invalid PUBLIC_ORIGIN protocol", async () => {
    process.env.PUBLIC_ORIGIN = "gopher://example.com";

    const request = new Request("http://localhost/onboarding", { method: "GET", headers: { origin: ORIGIN } });
    await expect(getCsrfToken(request)).rejects.toThrow("Invalid origin configuration.");
  });

  it("rejects PUBLIC_ORIGIN with user info", async () => {
    process.env.PUBLIC_ORIGIN = "https://user:pass@example.com";

    const request = new Request("https://example.com/onboarding", { method: "GET", headers: { origin: "https://example.com" } });
    await expect(getCsrfToken(request)).rejects.toThrow("Invalid origin configuration.");
  });

  it("rejects PUBLIC_ORIGIN with path/query/fragment", async () => {
    for (const origin of ["https://example.com/app", "https://example.com/?q=1", "https://example.com#frag"]) {
      process.env.PUBLIC_ORIGIN = origin;
      const request = new Request("https://example.com/onboarding", { method: "GET", headers: { origin: "https://example.com" } });
      await expect(getCsrfToken(request)).rejects.toThrow("Invalid origin configuration.");
    }
  });

  it("accepts loopback IP literals for default origin checks", async () => {
    const { token, cookies } = await createSeed();

    const request = createCsrfActionRequest(token, joinCookies(cookies), "http://127.0.0.1", "http://127.0.0.1/onboarding");
    await expect(validateCsrfToken(request, new URLSearchParams({ csrfToken: token }))).resolves.toMatchObject({ headers: expect.any(Object) });
  });

  it("rotates token and rejects replay", async () => {
    const { token, cookies } = await createSeed();

    const firstRequest = createCsrfActionRequest(token, joinCookies(cookies));
    await expect(validateCsrfToken(firstRequest, new URLSearchParams({ csrfToken: token }))).resolves.toMatchObject({ headers: expect.any(Object) });

    const replayRequest = createCsrfActionRequest(token, joinCookies(cookies));
    await expect(validateCsrfToken(replayRequest, new URLSearchParams({ csrfToken: token }))).rejects.toThrow();
  });

  it("recovers used token after cookie loss", async () => {
    const { token, cookies } = await createSeed();

    const firstRequest = createCsrfActionRequest(token, joinCookies(cookies));
    await validateCsrfToken(firstRequest, new URLSearchParams({ csrfToken: token }));

    const recoveryRequest = new Request("http://localhost/onboarding", {
      method: "GET",
      headers: {
        cookie: joinCookies(cookies),
      },
    });

    const refreshed = await getCsrfToken(recoveryRequest);
    expect(refreshed.headers).toBeDefined();
    expect(refreshed.token).not.toBe(token);
  });

  it("issues exactly one token for concurrent loads and consumes a token exactly once", async () => {
    const first = await createSeed();
    const cookie = joinCookies(first.cookies);
    const loads = await Promise.all([
      getCsrfToken(new Request("http://localhost/onboarding", { headers: { cookie } })),
      getCsrfToken(new Request("http://localhost/onboarding", { headers: { cookie } })),
    ]);
    expect(loads[0].token).toBe(first.token);
    expect(loads[1].token).toBe(first.token);
    expect(loads[0].headers).toBeUndefined();
    expect(loads[1].headers).toBeUndefined();

    const results = await Promise.allSettled([
      validateCsrfToken(createCsrfActionRequest(first.token, cookie), new URLSearchParams({ csrfToken: first.token })),
      validateCsrfToken(createCsrfActionRequest(first.token, cookie), new URLSearchParams({ csrfToken: first.token })),
    ]);
    expect(results.filter((result) => result.status === "fulfilled")).toHaveLength(1);
    expect(results.filter((result) => result.status === "rejected")).toHaveLength(1);
  });

  it("removes expired expected states and never accepts their old token", async () => {
    vi.useFakeTimers();
    try {
      const initial = await createSeed();
      vi.advanceTimersByTime(10 * 60 * 1000 + 1);
      const refreshed = await getCsrfToken(new Request("http://localhost/onboarding", { headers: { cookie: joinCookies(initial.cookies) } }));
      expect(refreshed.token).not.toBe(initial.token);
      await expect(validateCsrfToken(
        createCsrfActionRequest(initial.token, joinCookies(initial.cookies)),
        new URLSearchParams({ csrfToken: initial.token }),
      )).rejects.toThrow();
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not let cookie-less attackers exhaust a valid session", async () => {
    const victim = await createSeed();
    for (let i = 0; i < 600; i++) {
      await getCsrfToken(new Request("http://localhost/onboarding", {
        method: "GET",
        headers: { origin: ORIGIN },
      }));
    }

    await expect(validateCsrfToken(
      createCsrfActionRequest(victim.token, joinCookies(victim.cookies)),
      new URLSearchParams({ csrfToken: victim.token }),
    )).resolves.toMatchObject({ headers: expect.any(Object) });
  });

  it("tracks token issuance limits per source and preserves untouched token at cap", async () => {
    const victim = await createSeed();
    const untouched = await createSeed();
    const untouchedToken = untouched.token;

    let currentToken = victim.token;
    let currentCookie = joinCookies(victim.cookies);

    for (let i = 0; i < 255; i++) {
      const rotateRequest = createCsrfActionRequest(currentToken, currentCookie, ORIGIN, "http://localhost/onboarding");
      const rotateResponse = await validateCsrfToken(rotateRequest, new URLSearchParams({ csrfToken: currentToken }));
      const rotateCookies = getCookies(rotateResponse.headers);
      const reloaded = await getCsrfToken(new Request("http://localhost/onboarding", { method: "GET", headers: { cookie: joinCookies(rotateCookies), origin: ORIGIN } }));
      currentToken = reloaded.token;
      currentCookie = joinCookies(rotateCookies);
    }

    const exhausted = createCsrfActionRequest(currentToken, currentCookie, ORIGIN, "http://localhost/onboarding");
    await expect(validateCsrfToken(exhausted, new URLSearchParams({ csrfToken: currentToken }))).rejects.toThrow("CSRF token issuance rate exceeded.");

    await expect(validateCsrfToken(
      createCsrfActionRequest(untouchedToken, joinCookies(untouched.cookies), ORIGIN, "http://localhost/onboarding"),
      new URLSearchParams({ csrfToken: untouchedToken }),
    )).resolves.toMatchObject({ headers: expect.any(Object) });
  });
});

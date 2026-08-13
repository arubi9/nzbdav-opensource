import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as backendClientModule from "~/clients/backend-client.server";
import { action, loader } from "./route";
import { getCsrfToken } from "~/onboarding/onboarding-csrf.server";
import type { Route } from "./+types/route";

const getCookie = (headers?: HeadersInit): string => {
  const merged = new Headers(headers || {});
  return merged.get("Set-Cookie") || merged.get("set-cookie") || "";
};

const getSetCookies = (headers?: HeadersInit): string[] => {
  const merged = new Headers(headers || {});
    if (typeof (merged as Headers).getSetCookie === "function") {
    return (merged as Headers).getSetCookie();
  }
  const cookie = merged.get("Set-Cookie") || merged.get("set-cookie") || "";
  return cookie ? [cookie] : [];
};

const createLoginRequest = (csrfToken: string, cookie: string, body: Record<string, string>): Route.ActionArgs["request"] => {
  const payload = new URLSearchParams(body);
  payload.set("csrfToken", csrfToken);

  return new Request("http://localhost/login", {
    method: "POST",
    headers: {
      origin: "http://localhost",
      "content-type": "application/x-www-form-urlencoded",
      "content-length": String(Buffer.byteLength(payload.toString())),
      cookie,
    },
    body: payload,
  }) as Route.ActionArgs["request"];
};

describe("login route", () => {
  beforeEach(() => {
    vi.spyOn(backendClientModule.backendClient, "isOnboarding").mockResolvedValue(false);
    vi.spyOn(backendClientModule.backendClient, "authenticate").mockResolvedValue(true);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("redirects to onboarding when onboarding mode is active and sets no-store cache header", async () => {
    (backendClientModule.backendClient.isOnboarding as any).mockResolvedValue(true);

    const response = (await loader({ request: new Request("http://localhost/login", { method: "GET", headers: { origin: "http://localhost" } }), params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/login"), pattern: "/login" }) ) as Response;
    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/onboarding");
    expect(response.headers.get("Cache-Control")).toContain("no-store");
    expect(response.headers.get("Cache-Control")).toContain("private");
  });

  it("returns a CSRF token for the login form", async () => {
    const response = await loader({ request: new Request("http://localhost/login", { method: "GET", headers: { origin: "http://localhost" } }), params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/login"), pattern: "/login" }) as Response;
    const payload = await response.json();

    expect(payload.loginError).toBeNull();
    expect(payload.csrfToken).toBeTruthy();
  });

  it("rejects invalid payloads", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/login", { method: "GET", headers: { origin: "http://localhost" } }));

    const request = new Request("http://localhost/login", {
      method: "POST",
      headers: {
        origin: "http://localhost",
        "content-type": "application/x-www-form-urlencoded",
        cookie: getCookie(csrf.headers),
      },
      body: "", // missing content-length
    }) as Route.ActionArgs["request"];

    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/login"), pattern: "/login" })) as Response;
    const payload = await response.json();

    expect(payload.loginError).toBe("Invalid request payload.");
  });

  it("rejects invalid CSRF tokens", async () => {
    const request = createLoginRequest("bad-token", "", {
      username: "admin",
      password: "password",
    });

    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/login"), pattern: "/login" })) as Response;
    const payload = await response.json();

    expect(payload.loginError).toBe("CSRF token is missing or invalid.");
  });

  it("returns credential errors from authentication service", async () => {
    (backendClientModule.backendClient.authenticate as any).mockResolvedValue(false);

    const csrf = await getCsrfToken(new Request("http://localhost/login", { method: "GET", headers: { origin: "http://localhost" } }));
    const request = createLoginRequest(csrf.token, getCookie(csrf.headers), { username: "admin", password: "wrong" });

    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/login"), pattern: "/login" }) as Response);
    const payload = await response.json();

    expect(payload.loginError).toBe("Invalid credentials.");
  });

  it("logs in successfully and returns merged auth+csrf cookies with no-store cache", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/login", { method: "GET", headers: { origin: "http://localhost" } }));
    const request = createLoginRequest(csrf.token, getCookie(csrf.headers), { username: "admin", password: "pass" });

    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/login"), pattern: "/login" }) as Response);
    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/");
    expect(response.headers.get("Cache-Control")).toContain("no-store");
    expect(response.headers.get("Cache-Control")).toContain("private");
    expect(getSetCookies(response.headers).length).toBe(2);
  });
});

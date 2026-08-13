import { beforeEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";
import * as backendClientModule from "~/clients/backend-client.server";
import { createSetupSession } from "~/onboarding/setup-session.server";
import { getCsrfToken } from "~/onboarding/onboarding-csrf.server";
import type { Route } from "./+types/route";

const futureIso = (): string => {
  return new Date(Date.now() + 60_000).toISOString();
};

const getCookie = (headers?: HeadersInit): string => {
  const merged = new Headers(headers || {});
  const cookieHeader = merged.get("Set-Cookie") || merged.get("set-cookie") || "";
  return cookieHeader ? cookieHeader.split(";")[0] : "";
};

const createLogoutRequest = (csrfToken: string, cookie: string): Route.ActionArgs["request"] => {
  const body = new URLSearchParams({ csrfToken }).toString();

  return new Request("http://localhost/logout", {
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

describe("logout route", () => {
  const revokeSpy = vi.spyOn(backendClientModule.backendClient, "revokeSetupGrant");

  beforeEach(() => {
    revokeSpy.mockReset();
  });


  it("redirects to login when revoke responds as already revoked", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/logout", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET" }), "logout-grant", futureIso());

    revokeSpy.mockRejectedValue({ status: 410 } as { status?: number });

    const request = createLogoutRequest(csrf.token, `${getCookie(csrf.headers)}; ${getCookie(setup.headers)}`);
    const response = await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/logout"), pattern: "/logout" });

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/login");
  });

  it("always clears local auth/setup sessions even if revoke fails", async () => {
    const csrf = await getCsrfToken(new Request("http://localhost/logout", { method: "GET", headers: { origin: "http://localhost" } }));
    const setup = await createSetupSession(new Request("http://localhost/onboarding", { method: "GET" }), "logout-grant", futureIso());

    revokeSpy.mockRejectedValue({ status: 500 } as { status?: number });

    const request = createLogoutRequest(csrf.token, `${getCookie(csrf.headers)}; ${getCookie(setup.headers)}`);
    const response = (await action({ request, params: {}, context: { VALUE_FROM_EXPRESS: "test" }, url: new URL("http://localhost/logout"), pattern: "/logout" })) as Response;

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/login?warn=1");
  });
});

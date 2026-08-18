import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  __resetAuthenticationStateForTests,
  getAuthenticatedSession,
  isAuthenticated,
  logout,
  revokeAllSessions,
  setSessionUser,
} from "./authentication.server";

const cookieFrom = (headers?: HeadersInit): string => {
  const value = new Headers(headers || {}).get("set-cookie") || "";
  return value.split(";", 1)[0];
};

const requestWithCookie = (cookie: string): Request => new Request("http://localhost/ws", {
  headers: { cookie },
});

describe("frontend websocket session lifecycle", () => {
  beforeEach(() => {
    __resetAuthenticationStateForTests();
  });

  it("notifies a bound session on logout and supports cleanup", async () => {
    const created = await setSessionUser(new Request("http://localhost/login"), "operator");
    const cookie = cookieFrom(created.headers);
    const session = getAuthenticatedSession(requestWithCookie(cookie));
    expect(session?.id).toBeTruthy();

    let notifications = 0;
    const unsubscribe = session!.subscribe(() => { notifications += 1; });
    await logout(requestWithCookie(cookie));
    expect(notifications).toBe(1);
    expect(await isAuthenticated(requestWithCookie(cookie))).toBe(false);

    unsubscribe();
    await logout(requestWithCookie(cookie));
    expect(notifications).toBe(1);
  });

  it("removes an expired session and notifies its subscribers", async () => {
    vi.useFakeTimers();
    try {
      const created = await setSessionUser(new Request("http://localhost/login"), "operator");
      const cookie = cookieFrom(created.headers);
      const session = getAuthenticatedSession(requestWithCookie(cookie));
      let notifications = 0;
      session!.subscribe(() => { notifications += 1; });
      vi.advanceTimersByTime(12 * 60 * 60 * 1000 + 1);
      expect(getAuthenticatedSession(requestWithCookie(cookie))).toBeNull();
      expect(notifications).toBe(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("notifies replacement and revoke-all without a global per-message scan", async () => {
    const first = await setSessionUser(new Request("http://localhost/login"), "operator");
    const firstCookie = cookieFrom(first.headers);
    const firstSession = getAuthenticatedSession(requestWithCookie(firstCookie));
    let replacementNotifications = 0;
    firstSession!.subscribe(() => { replacementNotifications += 1; });

    // Login replacement receives the old cookie while issuing the new one.
    await setSessionUser(requestWithCookie(firstCookie), "operator");
    expect(replacementNotifications).toBe(1);

    const second = await setSessionUser(new Request("http://localhost/login"), "operator-2");
    const secondSession = getAuthenticatedSession(requestWithCookie(cookieFrom(second.headers)));
    let revokeNotifications = 0;
    secondSession!.subscribe(() => { revokeNotifications += 1; });
    revokeAllSessions();
    expect(revokeNotifications).toBe(1);
    expect(await isAuthenticated(requestWithCookie(cookieFrom(second.headers)))).toBe(false);
  });
  it("rejects a tampered opaque cookie without treating it as a new session", async () => {
    const created = await setSessionUser(new Request("http://localhost/login"), "operator");
    const cookie = cookieFrom(created.headers);
    const tampered = cookie.replace(/(__session=)([A-Za-z0-9_-])/, (_whole, prefix: string, first: string) =>
      `${prefix}${first === "A" ? "B" : "A"}`,
    );
    expect(tampered).not.toBe(cookie);
    expect(await isAuthenticated(requestWithCookie(tampered))).toBe(false);
  });

});

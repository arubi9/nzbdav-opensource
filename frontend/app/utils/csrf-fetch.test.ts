/**
 * @vitest-environment jsdom
 */
import { beforeEach, describe, expect, it, vi } from "vitest";
import { csrfFetch } from "./csrf-fetch";

const response = (status: number, token?: string): Response => new Response(
  status === 200 ? JSON.stringify({ ok: true }) : JSON.stringify({ error: "CSRF validation failed." }),
  { status, headers: { "Content-Type": "application/json", ...(token ? { "X-CSRF-Token": token } : {}) } },
);

describe("csrfFetch settings replay", () => {
  beforeEach(() => {
    document.body.innerHTML = '<meta name="csrf-token" content="old-token">';
    vi.restoreAllMocks();
  });

  it("replays an explicitly idempotent settings body once with the fresh token", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch");
    fetchMock
      .mockResolvedValueOnce(response(403, "fresh-token"))
      .mockResolvedValueOnce(response(200, "fresh-token"))
      .mockResolvedValueOnce(response(200, "next-token"));
    const body = JSON.stringify({ providers: [{ password: "draft-secret" }] });

    const result = await csrfFetch("/settings/usenet", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body,
    }, { idempotent: true });

    expect(result.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect((fetchMock.mock.calls[0]?.[1]?.headers as Headers).get("X-CSRF-Token")).toBe("old-token");
    expect(fetchMock.mock.calls[1]?.[0]).toBe("/api/csrf-token");
    expect((fetchMock.mock.calls[2]?.[1]?.headers as Headers).get("X-CSRF-Token")).toBe("fresh-token");
    expect(fetchMock.mock.calls[2]?.[1]?.body).toBe(body);
    expect(document.querySelector('meta[name="csrf-token"]')?.getAttribute("content")).toBe("next-token");
  });

  it("stops after one failed replay and never retries generic mutations", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch");
    fetchMock
      .mockResolvedValueOnce(response(403, "fresh-token"))
      .mockResolvedValueOnce(response(200, "fresh-token"))
      .mockResolvedValueOnce(response(403, "fresh-token"));

    const result = await csrfFetch("/settings/update", { method: "POST", body: "draft" }, { idempotent: true });
    expect(result.status).toBe(403);
    expect(fetchMock).toHaveBeenCalledTimes(3);

    fetchMock.mockReset();
    fetchMock
      .mockResolvedValueOnce(response(403, "fresh-token"))
      .mockResolvedValueOnce(response(200, "fresh-token"));
    const generic = await csrfFetch("/api/delete", { method: "POST", body: "mutation" });
    expect(generic.status).toBe(403);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1]?.[0]).toBe("/api/csrf-token");
  });
});

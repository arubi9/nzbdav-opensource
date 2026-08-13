import { beforeEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";

const mockValidateCsrfToken = vi.fn();
const mockGetCsrfToken = vi.fn();
const mockUpdateUsenetSettings = vi.fn();

vi.mock("~/auth/authentication.server", () => ({
  isAuthenticated: vi.fn(async () => true),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    updateUsenetSettings: (...args: unknown[]) => mockUpdateUsenetSettings(...args),
  },
}));

vi.mock("~/onboarding/onboarding-csrf.server", () => ({
  validateCsrfToken: (...args: unknown[]) => mockValidateCsrfToken(...args),
  getCsrfToken: (...args: unknown[]) => mockGetCsrfToken(...args),
}));

beforeEach(() => {
  vi.clearAllMocks();
});

describe("settings.usenet route action", () => {
  it("does not mutate on a stale CSRF token and returns the canonical fresh token", async () => {
    mockValidateCsrfToken.mockRejectedValue(new Error("stale"));
    mockGetCsrfToken.mockResolvedValue({ token: "fresh-token", headers: { "X-CSRF-Token": "fresh-token" } });
    mockUpdateUsenetSettings.mockResolvedValue({ providers: [], revision: "1" });

    const response = await action({
      request: new Request("http://localhost/settings.usenet", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "x-csrf-token": "old-token",
        },
        body: JSON.stringify({ revision: "1", providers: [] }),
      }),
      params: {},
      context: {},
    } as never);

    expect(response.status).toBe(403);
    expect(response.headers.get("X-CSRF-Token")).toBe("fresh-token");
    expect(mockUpdateUsenetSettings).toHaveBeenCalledTimes(0);
    expect(mockGetCsrfToken).toHaveBeenCalledTimes(1);
  });

  it("accepts mutation when CSRF token is valid", async () => {
    mockValidateCsrfToken.mockResolvedValue({ headers: { "X-CSRF-Token": "accepted-token" } });
    mockUpdateUsenetSettings.mockResolvedValue({ providers: [], revision: "1" });

    const response = await action({
      request: new Request("http://localhost/settings.usenet", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "x-csrf-token": "known-token",
        },
        body: JSON.stringify({ revision: "1", providers: [] }),
      }),
      params: {},
      context: {},
    } as never);

    expect(response.status).toBe(200);
    expect(mockUpdateUsenetSettings).toHaveBeenCalledTimes(1);
    expect(mockGetCsrfToken).not.toHaveBeenCalled();
    expect(response.headers.get("X-CSRF-Token")).toBe("accepted-token");
  });
});

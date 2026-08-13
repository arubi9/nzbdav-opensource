import { beforeEach, describe, expect, it, vi } from "vitest";
import { __sanitizeAdminSettingsPayload, action } from "./route";

const mockValidateCsrfToken = vi.fn();
const mockGetCsrfToken = vi.fn();
const mockUpdateAdminSettings = vi.fn();

vi.mock("~/auth/authentication.server", () => ({
    isAuthenticated: vi.fn(async () => true),
}));
vi.mock("~/clients/backend-client.server", () => ({
    backendClient: {
        updateAdminSettings: (...args: unknown[]) => mockUpdateAdminSettings(...args),
    },
}));
vi.mock("~/onboarding/onboarding-csrf.server", () => ({
    validateCsrfToken: (...args: unknown[]) => mockValidateCsrfToken(...args),
    getCsrfToken: (...args: unknown[]) => mockGetCsrfToken(...args),
}));

describe("settings.update action sanitization", () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it("does not mutate on a stale CSRF token and returns the canonical fresh token", async () => {
        mockValidateCsrfToken.mockRejectedValue(new Error("stale"));
        mockGetCsrfToken.mockResolvedValue({ token: "fresh-token", headers: { "X-CSRF-Token": "fresh-token" } });
        mockUpdateAdminSettings.mockResolvedValue({ config: {}, hasSecrets: {} });

        const response = await action({
            request: new Request("http://localhost/settings/update", {
                method: "POST",
                headers: { "Content-Type": "application/json", "x-csrf-token": "old-token" },
                body: JSON.stringify({ config: { "webdav.user": "operator" } }),
            }),
            params: {},
            context: {},
        } as never);

        expect(response.status).toBe(403);
        expect(response.headers.get("X-CSRF-Token")).toBe("fresh-token");
        expect(mockUpdateAdminSettings).not.toHaveBeenCalled();
    });

    it("rejects forbidden api.strm-key in config payload", () => {
        const payload = {
            config: {
                "api.strm-key": "blocked",
            },
            clearSecrets: [],
        };

        expect(__sanitizeAdminSettingsPayload(payload)).toBeNull();
    });

    it("rejects forbidden api.strm-key in config payload case-insensitively", () => {
        const payload = {
            config: {
                "API.STRM-KEY": "blocked",
            },
            clearSecrets: [],
        };

        expect(__sanitizeAdminSettingsPayload(payload)).toBeNull();
    });

    it("rejects forbidden api.strm-key in clear list", () => {
        const payload = {
            config: {},
            clearSecrets: ["webdav.pass", "api.strm-key"],
        };

        expect(__sanitizeAdminSettingsPayload(payload)).toBeNull();
    });

    it("accepts legitimate admin settings payloads", () => {
        const payload = {
            config: {
                "webdav.pass": "secret",
                "arr.instances": "{}",
            },
            clearSecrets: ["api.key"],
        };

        const sanitized = __sanitizeAdminSettingsPayload(payload);
        expect(sanitized).toEqual(payload);
    });
});

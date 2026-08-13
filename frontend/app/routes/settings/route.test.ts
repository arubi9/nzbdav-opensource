import { describe, expect, it } from "vitest";
import type { AdminSettingsResponse, UsenetSettingsResponse } from "~/clients/backend-client.server";
import type { ConnectionDetails } from "./usenet/usenet";
import { buildUsenetRequestPayload, defaultConfig, deriveHasSecrets, getChangedConfig, mergeUsenetDraftWithSavedPasses, setSecretClearState } from "./route";


describe("settings route helpers", () => {
    it("builds a changed config payload without clear-secret keys", () => {
        const config = {
            ...defaultConfig,
            "cache.max-size-gb": "10",
            "webdav.pass": "old",
            "cache.l2.access-key": "present",
        };

        const updated = {
            ...config,
            "cache.max-size-gb": "20",
            "webdav.pass": "replacement",
            "cache.l2.access-key": "updated",
        };

        const changed = getChangedConfig(config, updated, new Set(["webdav.pass", "cache.l2.access-key"]));

        expect(changed).toMatchObject({ "cache.max-size-gb": "20" });
        expect(changed).not.toHaveProperty("webdav.pass");
        expect(changed).not.toHaveProperty("cache.l2.access-key");
    });

    it("updates clear-secret state immutably", () => {
        let state = new Set<string>(["api.key"]);
        const setter = setSecretClearState((updater) => {
            state = typeof updater === "function"
                ? updater(state)
                : new Set(Array.from(updater));
        });

        setter("webdav.pass", true);
        expect(state.has("webdav.pass")).toBe(true);

        setter("webdav.pass", false);
        expect(state.has("webdav.pass")).toBe(false);
    });

    it("prefers server-provided has-secrets state from save response", () => {
        const baseline = {
            "api.key": true,
            "webdav.pass": true,
        };
        const response: AdminSettingsResponse = {
            config: { ...defaultConfig },
            hasSecrets: {
                "api.key": false,
                "webdav.pass": false,
                "arr.instances": false,
                "cache.l2.access-key": false,
                "cache.l2.secret-key": false,
            },
        };

        expect(deriveHasSecrets(response, baseline)).toMatchObject({
            "api.key": false,
            "webdav.pass": false,
        });
    });

    it("builds usenet request payloads with exact password fields", () => {
        const providers: ConnectionDetails[] = [
            {
                Id: "existing-provider",
                Host: "news.example.com",
                Port: 563,
                UseSsl: true,
                User: "existing-user",
                Pass: "",
                MaxConnections: 4,
                Type: 0,
                HasPassword: true,
            },
            {
                Id: "existing-provider-2",
                Host: "news2.example.com",
                Port: 563,
                UseSsl: false,
                User: "new-user",
                Pass: "rotated",
                MaxConnections: 8,
                Type: 0,
                HasPassword: false,
            },
        ];

        const payload = buildUsenetRequestPayload("revision-3", providers);
        expect(payload.revision).toBe("revision-3");
        expect(payload.providers).toHaveLength(2);
        expect(payload.providers[0]).not.toHaveProperty("password");
        expect(payload.providers[0]).not.toHaveProperty("HasPassword");
        expect(payload.providers[1]).toMatchObject({ password: "rotated" });
    });

    it("preserves unsaved provider passwords until success while merging saved state", () => {
        const savedUsenet: UsenetSettingsResponse = {
            revision: "new-revision",
            providers: [
                {
                    id: "existing-provider",
                    host: "news.example.com",
                    port: 563,
                    ssl: true,
                    user: "existing-user",
                    max: 4,
                    type: 0,
                    hasPassword: true,
                },
                {
                    id: "new-provider",
                    host: "news2.example.com",
                    port: 563,
                    ssl: false,
                    user: "new-user",
                    max: 8,
                    type: 0,
                    hasPassword: true,
                },
            ],
        };

        const draft: ConnectionDetails[] = [
            {
                Id: "existing-provider",
                Host: "news.example.com",
                Port: 563,
                UseSsl: true,
                User: "existing-user",
                Pass: "password-rotated",
                MaxConnections: 4,
                Type: 0,
                HasPassword: true,
            },
            {
                Id: undefined,
                Host: "news2.example.com",
                Port: 563,
                UseSsl: false,
                User: "new-user",
                Pass: "new-provider-password",
                MaxConnections: 8,
                Type: 0,
                HasPassword: false,
            },
        ];

        const merged = mergeUsenetDraftWithSavedPasses(savedUsenet, draft);
        expect(merged[0].Pass).toBe("password-rotated");
        expect(merged[1].Pass).toBe("new-provider-password");
        expect(merged).toEqual([
            expect.objectContaining({ Id: "existing-provider", HasPassword: true }),
            expect.objectContaining({ Id: "new-provider", HasPassword: true }),
        ]);
    });
});

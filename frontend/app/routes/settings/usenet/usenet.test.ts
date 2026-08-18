import { describe, expect, it } from "vitest";
import { addProvider, editProvider, isUsenetSettingsUpdated, removeProvider, reorderProviders, type ConnectionDetails } from "./usenet";

describe("usenet helpers", () => {
    const baseProvider: ConnectionDetails = {
        Type: 1,
        Host: "example.com",
        Port: 119,
        UseSsl: false,
        User: "user",
        Pass: "secret",
        MaxConnections: 4,
        Id: "id-1",
    };

    it("adds provider immutably", () => {
        const original = [baseProvider];
        const added = addProvider(original, { ...baseProvider, Host: "added.example.com", Id: "id-2" });

        expect(added).not.toBe(original);
        expect(added).toHaveLength(2);
        expect(original).toHaveLength(1);
        expect(added[0]).toBe(original[0]);
        expect(added[1]).not.toBe(baseProvider);
    });

    it("edits provider immutably with new references", () => {
        const original = [baseProvider, { ...baseProvider, Host: "other", Id: "id-2" }];
        const edited = editProvider(original, 1, { ...baseProvider, Host: "updated.example.com", Id: "id-2", Pass: "new" });

        expect(edited).not.toBe(original);
        expect(edited[0]).toBe(original[0]);
        expect(edited[1]).not.toBe(original[1]);
        expect(edited[1]).toMatchObject({ Host: "updated.example.com" });
        expect(original[1].Host).toBe("other");
    });

    it("removes provider immutably", () => {
        const original = [
            { ...baseProvider, Id: "id-1" },
            { ...baseProvider, Id: "id-2", Host: "second" },
            { ...baseProvider, Id: "id-3", Host: "third" },
        ];
        const removed = removeProvider(original, 1);

        expect(removed).not.toBe(original);
        expect(removed).toHaveLength(2);
        expect(removed).toMatchObject([{ Id: "id-1" }, { Id: "id-3" }]);
        expect(original).toHaveLength(3);
    });

    it("reorders providers immutably", () => {
        const original = [
            { ...baseProvider, Id: "id-1" },
            { ...baseProvider, Id: "id-2", Host: "second" },
            { ...baseProvider, Id: "id-3", Host: "third" },
        ];
        const reordered = reorderProviders(original, 0, 2);

        expect(reordered).not.toBe(original);
        expect(reordered).toHaveLength(3);
        expect(reordered[2]).toHaveProperty("Id", "id-1");
        expect(reordered[0]).toHaveProperty("Id", "id-2");
        expect(reordered[1]).toHaveProperty("Id", "id-3");
        expect(original).toHaveLength(3);
    });

    it("redacts pass in usenet change detection and detects explicit pass changes", () => {
        const saved = [
            { ...baseProvider, Id: "id-1", Pass: "" },
            { ...baseProvider, Id: "id-2", Host: "second", Pass: "" },
        ];
        const unchangedFields = [
            { ...baseProvider, Id: "id-1", Host: "example.com", Pass: "" },
            { ...baseProvider, Id: "id-2", Host: "second", Pass: "" },
        ];
        const metadataUpdate = [
            { ...baseProvider, Id: "id-1", Host: "other", Pass: "" },
            { ...baseProvider, Id: "id-2", Host: "second", Pass: "" },
        ];
        const passOnly = [
            { ...baseProvider, Id: "id-1", Pass: "rotated" },
            { ...baseProvider, Id: "id-2", Host: "second", Pass: "" },
        ];

        expect(isUsenetSettingsUpdated(saved, unchangedFields)).toBe(false);
        expect(isUsenetSettingsUpdated(saved, metadataUpdate)).toBe(true);
        expect(isUsenetSettingsUpdated(saved, passOnly)).toBe(true);
    });
});

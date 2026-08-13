/**
 * @vitest-environment jsdom
 */
import { act } from "react";
import { createRoot } from "react-dom/client";
import { useState } from "react";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { ProviderType, type ConnectionDetails, UsenetSettings } from "./usenet";

function Harness({ initialProviders }: { initialProviders: ConnectionDetails[] }) {
    const [providers, setProviders] = useState(initialProviders);

    return <UsenetSettings providers={providers} setProviders={setProviders} />;
}

const createProvider = (overrides: Partial<ConnectionDetails> = {}): ConnectionDetails => ({
    Type: ProviderType.Pooled,
    Host: "",
    Port: 443,
    UseSsl: true,
    User: "user",
    Pass: "",
    MaxConnections: 10,
    ...overrides,
});

describe("UsenetSettings reorder controls", () => {
    const originalWebSocket = globalThis.WebSocket;

    beforeEach(() => {
        vi.stubGlobal("WebSocket", class {
            send = vi.fn();
            close = vi.fn();
            onmessage: ((event: MessageEvent) => void) | null = null;
            onopen: (() => void) | null = null;
            onerror: ((event: Event) => void) | null = null;
            onclose: ((event: CloseEvent) => void) | null = null;
            constructor() {
                // noop
            }
        } as unknown as typeof WebSocket);
    });

    afterEach(() => {
        vi.unstubAllGlobals();
        if (originalWebSocket) {
            globalThis.WebSocket = originalWebSocket;
        }
    });

    it("renders reorder buttons, disables boundaries, and reorders providers in order", () => {
        const container = document.createElement("div");
        const providers = [
            createProvider({ Id: "provider-1", Host: "provider-1" }),
            createProvider({ Id: "provider-2", Host: "provider-2", User: "other" }),
        ];
        const root = createRoot(container);

        act(() => {
            root.render(<Harness initialProviders={providers} />);
        });

        const firstCard = container.querySelectorAll(".provider-card")[0]! as HTMLDivElement;
        const secondCard = container.querySelectorAll(".provider-card")[1]! as HTMLDivElement;

        const firstHostText = firstCard.querySelector(".provider-host")?.textContent;
        const secondHostText = secondCard.querySelector(".provider-host")?.textContent;
        expect(firstHostText).toBe("provider-1");
        expect(secondHostText).toBe("provider-2");

        const firstUp = firstCard.querySelector('button[data-reorder="up"]')! as HTMLButtonElement;
        const firstDown = firstCard.querySelector('button[data-reorder="down"]')! as HTMLButtonElement;
        const secondUp = secondCard.querySelector('button[data-reorder="up"]')! as HTMLButtonElement;
        const secondDown = secondCard.querySelector('button[data-reorder="down"]')! as HTMLButtonElement;

        expect(firstUp.disabled).toBe(true);
        expect(firstDown.disabled).toBe(false);
        expect(secondUp.disabled).toBe(false);
        expect(secondDown.disabled).toBe(true);

        act(() => {
            firstDown.click();
        });

        const reorderedFirstCard = container.querySelectorAll(".provider-card")[0]! as HTMLDivElement;
        const reorderedSecondCard = container.querySelectorAll(".provider-card")[1]! as HTMLDivElement;
        expect(reorderedFirstCard.querySelector(".provider-host")?.textContent).toBe("provider-2");
        expect(reorderedSecondCard.querySelector(".provider-host")?.textContent).toBe("provider-1");

        const newFirstUp = reorderedFirstCard.querySelector('button[data-reorder="up"]')! as HTMLButtonElement;
        const newFirstDown = reorderedFirstCard.querySelector('button[data-reorder="down"]')! as HTMLButtonElement;
        const newSecondUp = reorderedSecondCard.querySelector('button[data-reorder="up"]')! as HTMLButtonElement;
        const newSecondDown = reorderedSecondCard.querySelector('button[data-reorder="down"]')! as HTMLButtonElement;
        expect(newFirstUp.disabled).toBe(true);
        expect(newFirstDown.disabled).toBe(false);
        expect(newSecondUp.disabled).toBe(false);
        expect(newSecondDown.disabled).toBe(true);

        act(() => {
            root.unmount();
        });
    });

    it("supports moving the first row down via buttons and second row up", () => {
        const container = document.createElement("div");
        const providers = [
            createProvider({ Id: "provider-1", Host: "provider-1" }),
            createProvider({ Id: "provider-2", Host: "provider-2" }),
        ];
        const root = createRoot(container);

        act(() => {
            root.render(<Harness initialProviders={providers} />);
        });

        const firstCard = container.querySelectorAll(".provider-card")[0]! as HTMLDivElement;
        const secondCard = container.querySelectorAll(".provider-card")[1]! as HTMLDivElement;

        act(() => {
            (secondCard.querySelector('button[data-reorder="up"]') as HTMLButtonElement).click();
        });

        const updatedCards = container.querySelectorAll(".provider-card");
        expect(updatedCards[0]?.querySelector(".provider-host")?.textContent).toBe("provider-2");
        expect(updatedCards[1]?.querySelector(".provider-host")?.textContent).toBe("provider-1");

        act(() => {
            root.unmount();
        });
    });
});

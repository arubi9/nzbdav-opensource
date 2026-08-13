import { describe, expect, it, beforeEach } from "vitest";
import {
  FALLBACK_JELLYFIN_URL,
  getJellyfinSetupLinkHint,
  resolveBrowserJellyfinSetupUrl,
} from "./jellyfin-link.server";

const clearJellyfinEnv = () => {
  delete process.env.JELLYFIN_PUBLIC_URL;
  delete process.env.PUBLIC_JELLYFIN_URL;
  delete process.env.JELLYFIN_PUBLIC_PORT;
  delete process.env.PUBLIC_JELLYFIN_PORT;
  delete process.env.JELLYFIN_PORT;
};

describe("Jellyfin setup link helpers", () => {
  beforeEach(() => {
    clearJellyfinEnv();
  });

  it("accepts valid explicit public url and preserves path", () => {
    process.env.JELLYFIN_PUBLIC_URL = "https://media.example:9443/base/path/";
    const hint = getJellyfinSetupLinkHint();

    expect(hint.explicitPublicUrl).toBe("https://media.example:9443/base/path");
    expect(hint.publicPort).toBe(8096);
  });

  it("accepts IPv6 public urls and keeps brackets", () => {
    process.env.JELLYFIN_PUBLIC_URL = "http://[2001:db8::1]:8097/";
    const hint = getJellyfinSetupLinkHint();

    expect(hint.explicitPublicUrl).toBe("http://[2001:db8::1]:8097");
    expect(hint.publicPort).toBe(8096);
  });

  it("ignores invalid explicit URLs", () => {
    process.env.JELLYFIN_PUBLIC_URL = "ftp://example.com";
    const hint = getJellyfinSetupLinkHint();

    expect(hint.explicitPublicUrl).toBeNull();
  });

  it("rejects explicit urls with credentials, query, or hash", () => {
    process.env.PUBLIC_JELLYFIN_URL = "https://user:pw@example.com/path?x=1#top";
    const hint = getJellyfinSetupLinkHint();

    expect(hint.explicitPublicUrl).toBeNull();
  });

  it("validates public port and falls back when invalid", () => {
    process.env.JELLYFIN_PUBLIC_PORT = "70000";
    const hint = getJellyfinSetupLinkHint();

    expect(hint.publicPort).toBe(8096);
  });

  it("builds browser URL from location with IPv4 host", () => {
    (globalThis as any).window = {
      location: {
        protocol: "https:",
        hostname: "media.local",
      },
    };

    const hint = getJellyfinSetupLinkHint();
    expect(resolveBrowserJellyfinSetupUrl(hint)).toBe(`https://media.local:${hint.publicPort}`);
  });

  it("builds browser URL from IPv6 host using brackets", () => {
    (globalThis as any).window = {
      location: {
        protocol: "http:",
        hostname: "[2001:db8::1]",
      },
    };

    const hint = getJellyfinSetupLinkHint();
    expect(resolveBrowserJellyfinSetupUrl(hint)).toBe(`http://[2001:db8::1]:${hint.publicPort}`);
  });

  it("defaults to localhost with fallback hint when no window", () => {
    delete (globalThis as any).window;
    const hint = {
      explicitPublicUrl: null,
      publicPort: 8096,
    };

    expect(resolveBrowserJellyfinSetupUrl(hint)).toBe("");
    expect(FALLBACK_JELLYFIN_URL).toBe("http://localhost:8096");
  });
});

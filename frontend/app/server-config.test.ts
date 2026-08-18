import { describe, expect, it } from "vitest";
import { isTrustedProxyAddress, trustedProxyEntries, validateCookieConfig, validateFrontendBackendApiKey, validateStartupConfig } from "../server-config";

const productionEnv = {
  NODE_ENV: "production",
  SESSION_KEY: "production-session-key-012345678901234567890123",
  FRONTEND_BACKEND_API_KEY: "production-api-key-012345678901234567890123",
};

describe("server cookie/origin configuration", () => {
  it("requires an explicit backend websocket key and accepts the test fixture", () => {
    expect(() => validateFrontendBackendApiKey({ NODE_ENV: "test" })).toThrow("explicitly configured");
    expect(validateFrontendBackendApiKey({
      NODE_ENV: "test",
      FRONTEND_BACKEND_API_KEY: "frontend-test-backend-key-fixture-not-a-secret-0123456789",
    })).toContain("fixture-not-a-secret");
  });

  it("treats whitespace-only optional origins as unset", () => {
    const config = validateStartupConfig({
      ...productionEnv,
      PUBLIC_ORIGIN: "  ",
      JELLYFIN_PUBLIC_URL: "",
      PUBLIC_URL: "   ",
    });
    expect(config.publicOrigin).toBeUndefined();
    expect(config.secureCookies).toBe(true);
  });

  it("defaults production cookies to Secure and rejects invalid overrides", () => {
    expect(validateCookieConfig(productionEnv).secureCookies).toBe(true);
    expect(validateCookieConfig({ ...productionEnv, SECURE_COOKIES: "false" }).secureCookies).toBe(false);
    expect(() => validateCookieConfig({ ...productionEnv, PUBLIC_ORIGIN: "https://example.test", SECURE_COOKIES: "false" })).toThrow("SECURE_COOKIES");
    expect(() => validateCookieConfig({ ...productionEnv, SECURE_COOKIES: "yes" })).toThrow("SECURE_COOKIES");
  });

  it("accepts strict IP loopback/wildcard binds and rejects hostnames or ambiguous values", () => {
    expect(validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "127.0.0.1" }).bindAddress).toBe("127.0.0.1");
    expect(validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "::1" }).bindAddress).toBe("::1");
    expect(validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "0.0.0.0" }).bindAddress).toBe("0.0.0.0");
    expect(validateStartupConfig({ ...productionEnv, BIND_ADDRESS: "192.0.2.10" }).listenAddress).toBe("0.0.0.0");
    expect(() => validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "app.example" })).toThrow("BIND_ADDRESS");
    expect(() => validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "127.0.0.1:3000" })).toThrow("BIND_ADDRESS");
  });

  it("requires explicit insecure-cookie acknowledgement for non-loopback HTTP", () => {
    expect(() => validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "0.0.0.0", SECURE_COOKIES: "false" })).toThrow("NZBDAV_INSECURE_DEV_COOKIES");
    expect(validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "0.0.0.0", SECURE_COOKIES: "false", NZBDAV_INSECURE_DEV_COOKIES: "true" }).insecureDevCookies).toBe(true);
    for (const value of ["TRUE", "yes", "1"]) {
      expect(() => validateCookieConfig({ ...productionEnv, BIND_ADDRESS: "0.0.0.0", SECURE_COOKIES: "false", NZBDAV_INSECURE_DEV_COOKIES: value })).toThrow("NZBDAV_INSECURE_DEV_COOKIES");
    }
  });

  it("parses valid proxy CIDRs exactly and rejects malformed IP forms", () => {
    const env = { TRUSTED_PROXIES: "192.0.2.0/24,2001:db8::/32" };
    expect(isTrustedProxyAddress("192.0.2.44", env)).toBe(true);
    expect(isTrustedProxyAddress("192.0.3.1", env)).toBe(false);
    expect(isTrustedProxyAddress("2001:db8:1::1", env)).toBe(true);
    expect(isTrustedProxyAddress("2001:db9::1", env)).toBe(false);
    expect(isTrustedProxyAddress("::ffff:192.0.2.44", env)).toBe(true);
    for (const value of ["::::", "1:2", "2001:db8::1%eth0", "192.0.2.0/33"]) {
      expect(() => trustedProxyEntries({ TRUSTED_PROXIES: value })).toThrow("TRUSTED_PROXIES");
    }
  });

  it("normalizes IPv6-mapped proxy addresses and rejects malformed dotted tails safely", () => {
    const env = { TRUSTED_PROXIES: "::ffff:192.0.2.44/32,::ffff:c000:0201/32" };
    expect(isTrustedProxyAddress("::ffff:192.0.2.44", env)).toBe(true);
    expect(isTrustedProxyAddress("::ffff:c000:0201", env)).toBe(true);
    expect(isTrustedProxyAddress("::ffff:300.0.0.1", { TRUSTED_PROXIES: "127.0.0.1" })).toBe(false);
    expect(() => trustedProxyEntries({ TRUSTED_PROXIES: "::ffff:192.0.2.999/32" })).toThrow("TRUSTED_PROXIES");
  });
});

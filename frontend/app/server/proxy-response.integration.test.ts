import http from "node:http";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { once } from "node:events";

vi.mock("~/auth/authentication.server", () => ({
  isAuthenticated: vi.fn(async () => true),
}));

vi.mock("~/onboarding/onboarding-csrf.server", () => ({
  getCsrfToken: vi.fn(async () => ({ token: "get-token", headers: { "Set-Cookie": "ignored" } })),
  validateCsrfToken: vi.fn(async () => ({
    headers: {
      "X-CSRF-Token": "replacement-token",
    },
  })),
}));

let app: (typeof import("../../server/app")) ["app"];
let backend: http.Server;
let backendPort: number;

beforeAll(async () => {
  backend = http.createServer((req, res) => {
    if (req.url === "/api/proxy-auth-redir" && req.method === "POST") {
      res.statusCode = 302;
      res.setHeader("Location", "https://evil.example/redirect");
      res.setHeader("X-CSRF-Token", "backend-token");
      res.end("redirect");
      return;
    }
    res.statusCode = 200;
    res.end("ok");
  });
  backend.listen(0, "127.0.0.1");
  await once(backend, "listening");
  backendPort = (backend.address() as { port: number }).port;
  process.env.BACKEND_URL = `http://127.0.0.1:${backendPort}`;

  process.env.FRONTEND_BACKEND_API_KEY = "internal-api-key-for-proxy-test";
  process.env.TRUSTED_PROXIES = "127.0.0.1";
  ({ app } = await import("../../server/app"));
});

afterAll(async () => {
  backend.closeAllConnections();
  backend.close();
  await once(backend, "close");
  delete process.env.BACKEND_URL;
});

const listen = async () => {
  const server = app.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server;
};

const close = async (server: http.Server) => {
  server.close();
  await once(server, "close");
};

describe("proxy response hardening", () => {
  it("strips backend Location and preserves frontend CSRF token on proxy mutation", async () => {
    const server = await listen();
    try {
      const port = (server.address() as { port: number }).port;
      const response = await fetch(`http://127.0.0.1:${port}/api/proxy-auth-redir`, {
        method: "POST",
        headers: {
          "x-csrf-token": "submitted-token",
          "content-type": "application/json",
        },
        body: JSON.stringify({ hello: "world" }),
      });
      expect(response.status).toBe(302);
      expect(response.headers.get("Location")).toBeNull();
      expect(response.headers.get("X-CSRF-Token")).toBe("replacement-token");
    } finally {
      await close(server);
    }
  });
});

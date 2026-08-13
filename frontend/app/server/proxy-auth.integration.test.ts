import http from "node:http";
import { once } from "node:events";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { setSessionUser } from "~/auth/authentication.server";

let app: (typeof import("../../server/app"))["app"];
let backend: http.Server;
let backendPort: number;
let received: http.IncomingHttpHeaders | undefined;

beforeAll(async () => {
  backend = http.createServer((req, res) => {
    received = req.headers;
    req.resume();
    req.once("end", () => res.end("ok"));
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
});

const listen = async () => {
  const server = app.listen(0);
  await once(server, "listening");
  return server;
};

const close = async (server: http.Server) => {
  server.close();
  await once(server, "close");
};

const cookieForUser = async (): Promise<string> => {
  const response = await setSessionUser(new Request("http://localhost/login"), "proxy-test-user");
  return new Headers(response.headers).get("set-cookie") || "";
};

describe("proxy authentication boundary", () => {
  it("authenticates with the browser cookie before stripping credentials", async () => {
    const server = await listen();
    try {
      const cookie = await cookieForUser();
      const port = (server.address() as { port: number }).port;
      const response = await fetch(`http://127.0.0.1:${port}/api/proxy-read`, {
        headers: {
          Cookie: cookie,
          Authorization: "Bearer raw-browser-secret",
          "Proxy-Authorization": "Basic cmF3LXNlY3JldA==",
          "X-Setup-Grant": "raw-setup-grant",
          "X-CSRF-Token": "raw-csrf-token",
          "X-Api-Key": "raw-api-key",
        },
      });
      expect(response.status).toBe(200);
      expect(received?.cookie).toBeUndefined();
      expect(received?.authorization).toBeUndefined();
      expect(received?.["proxy-authorization"]).toBeUndefined();
      expect(received?.["x-setup-grant"]).toBeUndefined();
      expect(received?.["x-csrf-token"]).toBeUndefined();
      expect(received?.["x-api-key"]).toBe("internal-api-key-for-proxy-test");

      const csrfResponse = await fetch(`http://127.0.0.1:${port}/api/csrf-token`, { headers: { Cookie: cookie } });
      expect(csrfResponse.status).toBe(200);
      expect(csrfResponse.headers.get("cache-control")).toBe("no-store");
      expect(csrfResponse.headers.get("pragma")).toBe("no-cache");
      expect(csrfResponse.headers.get("expires")).toBe("0");
    } finally {
      await close(server);
    }
  });

  it("binds setup throttle source to trusted req.ip and ignores spoofed inbound source headers", async () => {
    const server = await listen();
    try {
      const cookie = await cookieForUser();
      const port = (server.address() as { port: number }).port;
      const common = {
        Cookie: cookie,
        "X-Frontend-Client-Source": "attacker-chosen-bucket",
      };
      const first = await fetch(`http://127.0.0.1:${port}/api/proxy-read`, {
        headers: { ...common, "X-Forwarded-For": "192.0.2.10" },
      });
      const firstSource = received?.["x-frontend-client-source"];
      const second = await fetch(`http://127.0.0.1:${port}/api/proxy-read`, {
        headers: { ...common, "X-Forwarded-For": "192.0.2.11" },
      });
      const secondSource = received?.["x-frontend-client-source"];
      expect(first.status).toBe(200);
      expect(second.status).toBe(200);
      expect(firstSource).toBeTruthy();
      expect(secondSource).toBeTruthy();
      expect(firstSource).not.toBe("attacker-chosen-bucket");
      expect(secondSource).not.toBe("attacker-chosen-bucket");
      expect(firstSource).not.toBe(secondSource);
    } finally {
      await close(server);
    }
  });

  it("denies an unauthenticated browser read without contacting the backend", async () => {
    received = undefined;
    const server = await listen();
    try {
      const port = (server.address() as { port: number }).port;
      const response = await fetch(`http://127.0.0.1:${port}/api/proxy-read`);
      expect(response.status).toBe(401);
      expect(received).toBeUndefined();
    } finally {
      await close(server);
    }
  });
});

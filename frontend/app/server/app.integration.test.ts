import http from "node:http";
import { once } from "node:events";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";

vi.mock("~/auth/authentication.server", () => ({
  isAuthenticated: vi.fn(async () => {
    await new Promise((resolve) => setTimeout(resolve, 100));
    return true;
  }),
}));
vi.mock("~/onboarding/onboarding-csrf.server", () => ({
  getCsrfToken: vi.fn(),
  validateCsrfToken: vi.fn(async () => {
    await new Promise((resolve) => setTimeout(resolve, 100));
    return { headers: { "X-CSRF-Result": "ok" } };
  }),
}));

let app: (typeof import("../../server/app"))["app"];
const body = Buffer.from(Array.from({ length: 64 * 1024 }, (_, i) => i % 251));
const API_LIMIT = 8 * 1024 * 1024;
let backend: http.Server;
let backendPort: number;
let backendRequests = 0;
let backendCompletions = 0;
let completedRequest: Promise<{ body: Buffer; headers: http.IncomingHttpHeaders }> | undefined;

beforeAll(async () => {
  backend = http.createServer((req, res) => {
    backendRequests++;
    const chunks: Buffer[] = [];
    completedRequest = new Promise((resolve) => {
      req.on("data", (chunk) => chunks.push(Buffer.from(chunk)));
      req.on("end", () => {
        backendCompletions++;
        resolve({ body: Buffer.concat(chunks), headers: req.headers });
        res.end("ok");
      });
    });
  });
  backend.listen(0, "127.0.0.1");
  await once(backend, "listening");
  backendPort = (backend.address() as { port: number }).port;
  process.env.BACKEND_URL = `http://127.0.0.1:${backendPort}`;
  ({ app } = await import("../../server/app"));
});

afterAll(async () => {
  backend.closeAllConnections();
  backend.close();
  await once(backend, "close");
});

const closeServer = async (server: http.Server) => {
  server.close();
  await once(server, "close");
};

const listenApp = async () => {
  const server = app.listen(0);
  await once(server, "listening");
  return server;
};

describe("mutation proxy body bounds", () => {
  it("does not lose a chunked body while auth and CSRF are slow", async () => {
    backendRequests = 0;
    backendCompletions = 0;
    completedRequest = undefined;
    const server = await listenApp();
    try {
      const port = (server.address() as { port: number }).port;
      const response = await new Promise<http.IncomingMessage>((resolve, reject) => {
        const request = http.request({ port, path: "/api/mutate", method: "POST", headers: {
          "Transfer-Encoding": "chunked", Cookie: "frontend=secret", "X-CSRF-Token": "token",
        } }, resolve);
        request.on("error", reject);
        for (let offset = 0; offset < body.length; offset += 4096) {
          request.write(body.subarray(offset, offset + 4096));
        }
        request.end();
      });
      expect(response.statusCode).toBe(200);
      const completion = completedRequest;
      expect(completion).toBeDefined();
      const received = await (completion as unknown as Promise<{ body: Buffer; headers: http.IncomingHttpHeaders }>);
      expect(received.body).toEqual(body);
      expect(received.headers.cookie).toBeUndefined();
      expect(received.headers["x-csrf-token"]).toBeUndefined();
      expect(received.headers["x-api-key"]).toBeDefined();
    } finally {
      await closeServer(server);
    }
  });

  it("rejects content-length over the cap without contacting the backend", async () => {
    backendRequests = 0;
    backendCompletions = 0;
    completedRequest = undefined;
    const server = await listenApp();
    try {
      const port = (server.address() as { port: number }).port;
      const status = await new Promise<number>((resolve, reject) => {
        const request = http.request({ port, path: "/api/mutate", method: "POST", headers: {
          "Content-Length": API_LIMIT + 1, "X-CSRF-Token": "token",
        } }, (response) => { resolve(response.statusCode ?? 0); response.resume(); });
        request.on("error", reject);
        request.end();
      });
      expect(status).toBe(413);
      expect(backendRequests).toBe(0);
      expect(backendCompletions).toBe(0);
    } finally {
      await closeServer(server);
    }
  }, 10_000);

  it("aborts a truly oversized chunked body without a completed backend mutation", async () => {
    backendRequests = 0;
    backendCompletions = 0;
    completedRequest = undefined;
    const server = await listenApp();
    try {
      const port = (server.address() as { port: number }).port;
      await new Promise<void>((resolve) => {
        const request = http.request({ port, path: "/api/mutate", method: "POST", headers: {
          "Transfer-Encoding": "chunked", "X-CSRF-Token": "token",
        } }, (response) => { response.resume(); resolve(); });
        request.on("error", () => resolve());
        const chunk = Buffer.alloc(64 * 1024, 7);
        let sent = 0;
        const write = () => {
          while (sent <= API_LIMIT && request.write(chunk)) sent += chunk.length;
          if (sent <= API_LIMIT) request.once("drain", write);
          else request.end();
        };
        write();
      });
      await new Promise((resolve) => setTimeout(resolve, 250));
      expect(backendCompletions).toBe(0);
    } finally {
      await closeServer(server);
    }
  });
});

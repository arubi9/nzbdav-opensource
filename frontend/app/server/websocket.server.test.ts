import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { once } from "node:events";
import http from "node:http";
import { WebSocket, WebSocketServer } from "ws";
import { isAllowedWebsocketOrigin, WEBSOCKET_MAX_PAYLOAD, WEBSOCKET_TOPICS, websocketServer } from "../../server/websocket.server";
import * as authModule from "../auth/authentication.server";

vi.mock("../auth/authentication.server", () => ({
  isAuthenticated: vi.fn(async () => true),
  getAuthenticatedSession: vi.fn(async () => undefined),
}));

const isAuthenticatedMock = vi.mocked(authModule.isAuthenticated);
const getAuthenticatedSessionMock = vi.mocked(authModule.getAuthenticatedSession);
const sessionListeners = new Set<() => void>();

const ORIGINAL_PUBLIC_ORIGIN = process.env.PUBLIC_ORIGIN;
const ORIGINAL_BACKEND_URL = process.env.BACKEND_URL;
const ORIGINAL_BACKEND_API_KEY = process.env.FRONTEND_BACKEND_API_KEY;
let backendServer: http.Server | undefined;
let backendWss: WebSocketServer | undefined;
let backendPort = 0;

const listenAndAttach = async (port?: number): Promise<{ server: http.Server; wss: WebSocketServer; port: number }> => {
  const server = http.createServer();
  const wss = new WebSocketServer({ noServer: true });
  websocketServer.initialize(wss);

  server.on("upgrade", (request, socket, head) => {
    if ((request.url || "").startsWith("/ws")) {
      wss.handleUpgrade(request, socket, head, (websocket) => wss.emit("connection", websocket, request));
    } else {
      socket.destroy();
    }
  });

  return new Promise((resolve, reject) => {
    server.listen(port || 0, "127.0.0.1", () => resolve({
      server,
      wss,
      port: (server.address() as { port: number }).port,
    }));
    server.once("error", reject);
  });
};

const listenAndAttachBackend = async (): Promise<{ server: http.Server; wss: WebSocketServer; port: number }> => {
  const server = http.createServer();
  const wss = new WebSocketServer({ noServer: true });

  server.on("upgrade", (request, socket, head) => {
    if ((request.url || "").startsWith("/ws")) {
      wss.handleUpgrade(request, socket, head, (websocket) => {
        websocket.close();
      });
      return;
    }
    socket.destroy();
  });

  return new Promise((resolve, reject) => {
    server.listen(0, "127.0.0.1", () => resolve({
      server,
      wss,
      port: (server.address() as { port: number }).port,
    }));
    server.once("error", reject);
  });
};

const listenAndAttachBackendWithState = async (): Promise<{ server: http.Server; wss: WebSocketServer; port: number }> => {
  const server = http.createServer();
  const wss = new WebSocketServer({ noServer: true });

  wss.on("connection", (socket) => {
    socket.on("message", (raw) => {
      let parsed: Record<string, string>;
      try {
        const topicPayload = JSON.parse(raw.toString()) as unknown;
        if (!topicPayload || typeof topicPayload !== "object" || Array.isArray(topicPayload)) return;
        parsed = topicPayload as Record<string, string>;
      } catch {
        return;
      }
      const firstTopic = Object.keys(parsed).find((topic) => WEBSOCKET_TOPICS.has(topic));
      if (firstTopic && parsed[firstTopic] === "state") {
        socket.send(JSON.stringify({
          Topic: firstTopic,
          Message: JSON.stringify({ ok: true }),
        }));
      }
    });
  });

  server.on("upgrade", (request, socket, head) => {
    if ((request.url || "").startsWith("/ws")) {
      wss.handleUpgrade(request, socket, head, (websocket) => {
        wss.emit("connection", websocket, request);
      });
      return;
    }
    socket.destroy();
  });

  return new Promise((resolve, reject) => {
    server.listen(0, "127.0.0.1", () => resolve({
      server,
      wss,
      port: (server.address() as { port: number }).port,
    }));
    server.once("error", reject);
  });
};

const listenAndAttachBackendWithEnvelope = async (
  sendMessage: (socket: WebSocket) => void,
): Promise<{ server: http.Server; wss: WebSocketServer; port: number }> => {
  const server = http.createServer();
  const wss = new WebSocketServer({ noServer: true });

  wss.on("connection", (socket) => {
    sendMessage(socket);
  });

  server.on("upgrade", (request, socket, head) => {
    if ((request.url || "").startsWith("/ws")) {
      wss.handleUpgrade(request, socket, head, (websocket) => {
        wss.emit("connection", websocket, request);
      });
      return;
    }
    socket.destroy();
  });

  return new Promise((resolve, reject) => {
    server.listen(0, "127.0.0.1", () => resolve({
      server,
      wss,
      port: (server.address() as { port: number }).port,
    }));
    server.once("error", reject);
  });
};

const closeServer = async (server: http.Server, wss: WebSocketServer): Promise<void> => {
  for (const client of wss.clients) {
    client.close();
  }
  wss.close();
  server.close();
  await once(server, "close");
};

const connectWs = (port: number, origin: string): Promise<WebSocket> => new Promise((resolve, reject) => {
  const socket = new WebSocket(`ws://127.0.0.1:${port}/ws`, {
    headers: {
      Origin: origin,
    },
  });
  const timer = setTimeout(() => {
    socket.terminate();
    reject(new Error("websocket connect timeout"));
  }, 2000);

  const done = (error?: Error): void => {
    clearTimeout(timer);
    if (error) reject(error);
    else resolve(socket);
  };

  socket.on("open", () => done());
  socket.once("error", done);
  socket.once("close", () => done(new Error("websocket closed before open")));
});

const waitForClose = (socket: WebSocket): Promise<{ code: number; reason: string }> => new Promise((resolve, reject) => {
  const timer = setTimeout(() => {
    reject(new Error("websocket close timeout"));
  }, 2000);
  const done = (code: number, reason: Buffer) => {
    clearTimeout(timer);
    resolve({ code, reason: reason.toString() });
  };
  socket.once("close", done);
  socket.once("error", () => {
    // ws may emit a close-driven error before close. Keep close as the
    // canonical signal for close assertion.
  });
});

const allowedOriginRequest = (host: string): Parameters<typeof isAllowedWebsocketOrigin>[0] => ({
  headers: {
    host,
  },
  socket: {
    remoteAddress: "127.0.0.1",
    encrypted: false,
  },
} as unknown as Parameters<typeof isAllowedWebsocketOrigin>[0]);

beforeAll(async () => {
  const { server, wss, port } = await listenAndAttachBackend();
  backendServer = server;
  backendWss = wss;
  backendPort = port;
  process.env.BACKEND_URL = `http://127.0.0.1:${backendPort}`;
  process.env.FRONTEND_BACKEND_API_KEY = "frontend-backend-key";
});

afterAll(async () => {
  if (backendWss) {
    backendWss.close();
  }
  if (backendServer) {
    backendServer.close();
    await once(backendServer, "close");
  }
});

beforeEach(() => {
  isAuthenticatedMock.mockReset();
  isAuthenticatedMock.mockResolvedValue(true);
  sessionListeners.clear();
  getAuthenticatedSessionMock.mockReset();
  getAuthenticatedSessionMock.mockResolvedValue({
    id: "test-session",
    revision: 1,
    expiresAt: Date.now() + 60_000,
    subscribe: (listener: () => void) => {
      sessionListeners.add(listener);
      return () => sessionListeners.delete(listener);
    },
  });
  process.env.BACKEND_URL = `http://127.0.0.1:${backendPort}`;
  process.env.FRONTEND_BACKEND_API_KEY = "frontend-backend-key";
});

afterEach(() => {
  if (ORIGINAL_PUBLIC_ORIGIN === undefined) {
    delete process.env.PUBLIC_ORIGIN;
  } else {
    process.env.PUBLIC_ORIGIN = ORIGINAL_PUBLIC_ORIGIN;
  }
  if (ORIGINAL_BACKEND_URL === undefined) {
    delete process.env.BACKEND_URL;
  } else {
    process.env.BACKEND_URL = ORIGINAL_BACKEND_URL;
  }
  if (ORIGINAL_BACKEND_API_KEY === undefined) {
    delete process.env.FRONTEND_BACKEND_API_KEY;
  } else {
    process.env.FRONTEND_BACKEND_API_KEY = ORIGINAL_BACKEND_API_KEY;
  }
  delete process.env.TRUSTED_PROXIES;
});

describe("websocket origin policy", () => {
  it("rejects missing, null, malformed, and cross-port websocket origins", () => {
    const req = allowedOriginRequest("127.0.0.1:3000");
    expect(isAllowedWebsocketOrigin(req, undefined)).toBe(false);
    expect(isAllowedWebsocketOrigin(req, "null")).toBe(false);
    expect(isAllowedWebsocketOrigin(req, "\thttp://127.0.0.1:3000\t")).toBe(false);

    expect(isAllowedWebsocketOrigin(req, "http://127.0.0.1:3000")).toBe(true);
    expect(isAllowedWebsocketOrigin(req, "http://127.0.0.1:3001")).toBe(false);
    expect(isAllowedWebsocketOrigin(req, "https://127.0.0.1:3000")).toBe(false);
    for (const malicious of [
      "http://127.0.0.1:3000/",
      "http://127.0.0.1:3000/path",
      "http://user:pass@127.0.0.1:3000",
      "http://127.0.0.1:3000?x=1",
      "http://127.0.0.1:3000#fragment",
      "http://127.0.0.1:3000, http://127.0.0.1:3000",
      "http://127.0.0.1:3000\u0000",
    ]) {
      expect(isAllowedWebsocketOrigin(req, malicious), malicious).toBe(false);
    }
  });

  it("rejects duplicate and conflicting trusted-proxy authority headers", () => {
    const req = allowedOriginRequest("internal.invalid:3000");
    const trusted = { PUBLIC_ORIGIN: "https://public.example", TRUSTED_PROXIES: "127.0.0.1" } as any;
    expect(isAllowedWebsocketOrigin({ ...req, headers: { host: "internal.invalid:3000", forwarded: "proto=https;host=public.example", "x-forwarded-host": "public.example" } } as any, "https://public.example", trusted)).toBe(false);
    expect(isAllowedWebsocketOrigin({ ...req, headers: { host: "internal.invalid:3000", "x-forwarded-host": "public.example,evil.example", "x-forwarded-proto": "https" } } as any, "https://public.example", trusted)).toBe(false);
    expect(isAllowedWebsocketOrigin({ ...req, headers: { host: "internal.invalid:3000", forwarded: "proto=https;host=public.example, proto=http;host=evil.example" } } as any, "https://public.example", trusted)).toBe(false);
  });

  it("requires a PUBLIC_ORIGIN match when configured and rejects mismatch", () => {
    const req = allowedOriginRequest("127.0.0.1:3000");
    expect(isAllowedWebsocketOrigin(req, "http://127.0.0.1:3000", { PUBLIC_ORIGIN: "https://127.0.0.1:3000" } as any)).toBe(false);
    expect(isAllowedWebsocketOrigin(req, "https://127.0.0.1:3000", { PUBLIC_ORIGIN: "https://127.0.0.1:3000" } as any)).toBe(true);
    expect(isAllowedWebsocketOrigin(req, "https://127.0.0.1:3001", { PUBLIC_ORIGIN: "https://127.0.0.1:3000" } as any)).toBe(false);
  });
});

describe("websocket server hardening", () => {
  it("rejects mismatched websocket origins during handshake", async () => {
    const { server, wss, port } = await listenAndAttach();
    try {
      process.env.PUBLIC_ORIGIN = `http://127.0.0.1:${port}`;
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      socket.close();
      await close;

      await expect(connectWs(port, `http://127.0.0.1:${port + 1}`)).rejects.toThrow();
    } finally {
      await closeServer(server, wss);
    }
  });

  it("closes an established socket immediately when its authenticated session is revoked", async () => {
    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      for (const listener of sessionListeners) listener();
      await expect(close).resolves.toMatchObject({ code: 1008 });
      expect(socket.readyState).toBe(WebSocket.CLOSED);
    } finally {
      await closeServer(server, wss);
    }
  });

  it("closes an established socket at its fixed session expiry", async () => {
    getAuthenticatedSessionMock.mockResolvedValueOnce({
      id: "expiring-session",
      revision: 3,
      expiresAt: Date.now() + 20,
      subscribe: (listener: () => void) => {
        sessionListeners.add(listener);
        return () => sessionListeners.delete(listener);
      },
    });
    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      await expect(close).resolves.toMatchObject({ code: 1008 });
    } finally {
      await closeServer(server, wss);
    }
  });

  it("drops invalid topic subscriptions", async () => {
    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      socket.send(JSON.stringify({ unknown: "state" }));
      await expect(close).resolves.toMatchObject({ code: 1003 });
    } finally {
      await closeServer(server, wss);
    }
  });

  it("enforces per-connection topic and message-rate limits", async () => {
    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const closeRate = waitForClose(socket);

      socket.send(JSON.stringify({ qs: "state" }));
      socket.send(JSON.stringify({ qr: "state" }));
      socket.send(JSON.stringify({ hr: "state" }));
      socket.send(JSON.stringify({ hs: "state" }));
      socket.send(JSON.stringify({ hp: "state" }));
      await expect(closeRate).resolves.toMatchObject({ code: 1008 });
    } finally {
      await closeServer(server, wss);
    }
  }, 20_000);

  it("enforces websocket payload cap and uses a finite allowlist", async () => {
    const { server, wss, port } = await listenAndAttach();
    try {
      expect(WEBSOCKET_MAX_PAYLOAD).toBe(64 * 1024);
      expect(WEBSOCKET_TOPICS.size).toBeGreaterThan(0);

      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const closePayload = waitForClose(socket);
      socket.send("x".repeat(WEBSOCKET_MAX_PAYLOAD + 1));
      await expect(closePayload).resolves.toMatchObject({ code: 1009 });
    } finally {
      await closeServer(server, wss);
    }
  });

  it("drops oversized backend frames before message fanout", async () => {
    const oversized = "x".repeat(WEBSOCKET_MAX_PAYLOAD + 100);
    let backendCloseCode = 0;
    const backend = await listenAndAttachBackendWithEnvelope((socket) => {
      socket.once("close", (code) => {
        backendCloseCode = code;
      });
      setTimeout(() => {
        socket.send(JSON.stringify({
          Topic: "qs",
          Message: oversized,
        }));
      }, 25);
    });
    const originalBackendUrl = process.env.BACKEND_URL;
    process.env.BACKEND_URL = `http://127.0.0.1:${backend.port}`;

    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      socket.send(JSON.stringify({ qs: "state" }));
      let messageCount = 0;
      socket.on("message", () => {
        messageCount += 1;
      });

      await new Promise((resolve) => setTimeout(resolve, 150));
      expect(messageCount).toBe(0);
      expect(backendCloseCode).toBe(1009);
      socket.close();
      await waitForClose(socket);
    } finally {
      await closeServer(server, wss);
      await closeServer(backend.server, backend.wss);
      if (originalBackendUrl === undefined) {
        delete process.env.BACKEND_URL;
      } else {
        process.env.BACKEND_URL = originalBackendUrl;
      }
    }
  });

  it("drops malformed backend websocket envelopes", async () => {
    const backend = await listenAndAttachBackendWithEnvelope((socket) => {
      socket.send("not-json");
    });
    const originalBackendUrl = process.env.BACKEND_URL;
    process.env.BACKEND_URL = `http://127.0.0.1:${backend.port}`;

    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      await expect(close).resolves.toMatchObject({ code: 1003 });
    } finally {
      await closeServer(server, wss);
      await closeServer(backend.server, backend.wss);
      if (originalBackendUrl === undefined) {
        delete process.env.BACKEND_URL;
      } else {
        process.env.BACKEND_URL = originalBackendUrl;
      }
    }
  });

  it("drops backend websocket envelopes with non-string Message", async () => {
    const backend = await listenAndAttachBackendWithEnvelope((socket) => {
      socket.send(JSON.stringify({ Topic: "qs", Message: { okay: true } }));
    });
    const originalBackendUrl = process.env.BACKEND_URL;
    process.env.BACKEND_URL = `http://127.0.0.1:${backend.port}`;

    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      await expect(close).resolves.toMatchObject({ code: 1003 });
    } finally {
      await closeServer(server, wss);
      await closeServer(backend.server, backend.wss);
      if (originalBackendUrl === undefined) {
        delete process.env.BACKEND_URL;
      } else {
        process.env.BACKEND_URL = originalBackendUrl;
      }
    }
  });

  it("rejects backend websocket envelopes with extra fields", async () => {
    const backend = await listenAndAttachBackendWithEnvelope((socket) => {
      socket.send(JSON.stringify({
        Topic: "qs",
        Message: "ok",
        Extra: true,
      }));
    });
    const originalBackendUrl = process.env.BACKEND_URL;
    process.env.BACKEND_URL = `http://127.0.0.1:${backend.port}`;

    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      await expect(close).resolves.toMatchObject({ code: 1003 });
    } finally {
      await closeServer(server, wss);
      await closeServer(backend.server, backend.wss);
      if (originalBackendUrl === undefined) {
        delete process.env.BACKEND_URL;
      } else {
        process.env.BACKEND_URL = originalBackendUrl;
      }
    }
  });

  it("drops binary backend websocket envelopes", async () => {
    const backend = await listenAndAttachBackendWithEnvelope((socket) => {
      socket.send(Buffer.from("backend"));
    });
    const originalBackendUrl = process.env.BACKEND_URL;
    process.env.BACKEND_URL = `http://127.0.0.1:${backend.port}`;

    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      const close = waitForClose(socket);
      await expect(close).resolves.toMatchObject({ code: 1003 });
    } finally {
      await closeServer(server, wss);
      await closeServer(backend.server, backend.wss);
      if (originalBackendUrl === undefined) {
        delete process.env.BACKEND_URL;
      } else {
        process.env.BACKEND_URL = originalBackendUrl;
      }
    }
  });

  it("forwards normalized backend envelopes", async () => {
    const backend = await listenAndAttachBackendWithEnvelope((socket) => {
      setTimeout(() => {
        socket.send(JSON.stringify({
          Topic: "qs",
          Message: "1|2|3",
        }));
      }, 25);
    });
    const originalBackendUrl = process.env.BACKEND_URL;
    process.env.BACKEND_URL = `http://127.0.0.1:${backend.port}`;

    const { server, wss, port } = await listenAndAttach();
    try {
      const socket = await connectWs(port, `http://127.0.0.1:${port}`);
      socket.send(JSON.stringify({ qs: "state" }));
      const message = await new Promise<string>((resolve, reject) => {
        socket.on("message", (event) => {
          resolve(event.toString());
        });
        socket.on("close", () => reject(new Error("socket closed before message")));
      });
      expect(JSON.parse(message)).toMatchObject({
        Topic: "qs",
        Message: "1|2|3",
      });
      const parsed = JSON.parse(message) as { Topic: string; Message: string };
      expect(Object.keys(parsed)).toEqual(["Topic", "Message"]);
      socket.close();
    } finally {
      await closeServer(server, wss);
      await closeServer(backend.server, backend.wss);
      if (originalBackendUrl === undefined) {
        delete process.env.BACKEND_URL;
      } else {
        process.env.BACKEND_URL = originalBackendUrl;
      }
    }
  });

  it("can reject cap-overflow sockets, reclaim capacity, and accept a replacement", async () => {
    for (let iteration = 0; iteration < 3; iteration += 1) {
      const backend = await listenAndAttachBackendWithState();
      const originalBackendUrl = process.env.BACKEND_URL;
      process.env.BACKEND_URL = `http://127.0.0.1:${backend.port}`;

      const { server, wss, port } = await listenAndAttach();
      const validOrigin = `http://127.0.0.1:${port}`;
      const sockets: WebSocket[] = [];
      let rejectedConnection: WebSocket | undefined;

      try {
        for (let i = 0; i < 256; i += 1) {
          sockets.push(await connectWs(port, validOrigin));
        }

        rejectedConnection = await connectWs(port, validOrigin);
        const rejectedClose = waitForClose(rejectedConnection);
        await expect(rejectedClose).resolves.toMatchObject({ code: 1013 });
        expect(rejectedConnection.readyState).toBe(WebSocket.CLOSED);
        expect(rejectedConnection.send.bind(rejectedConnection, JSON.stringify({ qs: "state" }))).not.toThrow();

        const removed = sockets.shift() as WebSocket;
        const removedClosed = waitForClose(removed);
        removed.close();
        await removedClosed;

        const replacement = await connectWs(port, validOrigin);
        replacement.send(JSON.stringify({ qs: "state" }));
        await new Promise(resolve => setTimeout(resolve, 25));
        expect(replacement.readyState).toBe(WebSocket.OPEN);

        const replacementClose = waitForClose(replacement);
        replacement.close();
        await replacementClose;
      } finally {
        if (rejectedConnection) {
          rejectedConnection.close();
        }
        for (const socket of sockets) {
          if (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING) {
            socket.close();
          }
          socket.removeAllListeners();
        }
        await closeServer(server, wss);
        await closeServer(backend.server, backend.wss);
        if (originalBackendUrl === undefined) {
          delete process.env.BACKEND_URL;
        } else {
          process.env.BACKEND_URL = originalBackendUrl;
        }
      }
    }
  }, 60_000);
});

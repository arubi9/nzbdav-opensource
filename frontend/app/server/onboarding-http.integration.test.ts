import http from "node:http";
import net from "node:net";
import { once } from "node:events";
import { spawn, type ChildProcessByStdio } from "node:child_process";
import type { Readable } from "node:stream";
import { existsSync, rmSync } from "node:fs";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

type BackendRequest = {
  path: string;
  method: string;
  headers: http.IncomingHttpHeaders;
  body: string;
};

let backend: http.Server;
let backendPort = 0;
type FrontendProcess = ChildProcessByStdio<null, Readable, Readable>;

let frontend: FrontendProcess;
let frontendPort = 0;
const backendRequests: BackendRequest[] = [];

const freePort = async (): Promise<number> => new Promise((resolve, reject) => {
  const probe = net.createServer();
  probe.once("error", reject);
  probe.listen(0, "127.0.0.1", () => {
    const address = probe.address() as net.AddressInfo;
    probe.close((error) => error ? reject(error) : resolve(address.port));
  });
});

const origin = (): string => `http://127.0.0.1:${frontendPort}`;
const sessionKey = "onboarding-http-integration-session-key-0123456789";
const sessionStorePath = join(
  tmpdir(),
  `nzbdav-frontend-sessions-${createHash("sha256").update(sessionKey).digest("hex").slice(0, 32)}`,
);

const setJson = (response: http.ServerResponse, status: number, value: unknown): void => {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body),
  });
  response.end(body);
};

const cookiesFrom = (response: Response): string[] => {
  const headers = response.headers as Headers & { getSetCookie?: () => string[] };
  return headers.getSetCookie?.() || (headers.get("set-cookie") ? [headers.get("set-cookie")!] : []);
};

const updateCookieJar = (response: Response, previous = ""): string => {
  const values = new Map<string, string>();
  for (const item of previous.split(/;\s*/)) {
    const separator = item.indexOf("=");
    if (separator > 0) values.set(item.slice(0, separator), item);
  }
  for (const cookie of cookiesFrom(response)) {
    const pair = cookie.split(";", 1)[0];
    const separator = pair.indexOf("=");
    if (separator > 0) values.set(pair.slice(0, separator), pair);
  }
  return [...values.values()].join("; ");
};

const waitForFrontend = async (child: FrontendProcess): Promise<void> => new Promise((resolve, reject) => {
  let output = "";
  const timeout = setTimeout(() => reject(new Error(`built frontend did not become ready: ${output}`)), 20_000);
  const onData = (chunk: Buffer | string) => {
    output += chunk.toString();
    if (output.includes("Server is running on")) {
      clearTimeout(timeout);
      resolve();
    }
  };
  child.stdout.on("data", onData);
  child.stderr.on("data", onData);
  child.once("error", (error) => {
    clearTimeout(timeout);
    reject(error);
  });
  child.once("exit", (code, signal) => {
    clearTimeout(timeout);
    reject(new Error(`built frontend exited before readiness (code ${code}, signal ${signal}): ${output}`));
  });
});

const startFrontend = async (): Promise<void> => {
  frontendPort = await freePort();
  frontend = spawn(process.execPath, ["dist-node/server.js"], {
    cwd: process.cwd(),
    env: {
      ...process.env,
      NODE_ENV: "production",
      PORT: String(frontendPort),
      BIND_ADDRESS: "127.0.0.1",
      FRONTEND_LISTEN_ADDRESS: "127.0.0.1",
      SECURE_COOKIES: "false",
      PUBLIC_ORIGIN: "",
      TRUSTED_PROXIES: "",
      SESSION_KEY: sessionKey,
      FRONTEND_BACKEND_API_KEY: "onboarding-http-integration-backend-key-0123456789",
      BACKEND_URL: `http://127.0.0.1:${backendPort}`,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  await waitForFrontend(frontend);
};

const restartFrontend = async (): Promise<void> => {
  frontend.kill();
  await once(frontend, "exit");
  await startFrontend();
};

beforeAll(async () => {
  if (!existsSync("dist-node/server.js") || !existsSync("build/server/index.js")) {
    throw new Error("built frontend is required; run npm run build && npm run build:server first");
  }
  rmSync(sessionStorePath, { force: true, recursive: true });

  backend = http.createServer(async (request, response) => {
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(Buffer.from(chunk));
    const body = Buffer.concat(chunks).toString("utf8");
    const path = new URL(request.url || "/", "http://backend.invalid").pathname;
    backendRequests.push({ path, method: request.method || "", headers: request.headers, body });

    if (path === "/api/setup/status" && request.method === "GET") {
      setJson(response, 200, {
        enabled: true,
        completed: false,
        steps: [],
        services: [],
        revocationPending: false,
        repairRequired: false,
      });
      return;
    }

    if (path === "/api/setup/handoff" && request.method === "POST") {
      setJson(response, 200, {
        grant: "integration-grant-only",
        expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      });
      return;
    }

    setJson(response, 404, { code: "not-found" });
  });
  backend.listen(0, "127.0.0.1");
  await once(backend, "listening");
  backendPort = (backend.address() as net.AddressInfo).port;

  await startFrontend();
}, 60_000);

afterAll(async () => {
  if (frontend && !frontend.killed) {
    frontend.kill();
    await once(frontend, "exit");
  }
  if (backend) {
    backend.closeAllConnections();
    backend.close();
    await once(backend, "close");
  }
  rmSync(sessionStorePath, { force: true, recursive: true });
}, 60_000);

describe("built public onboarding CSRF handoff", () => {
  it("accepts GET cookie/token then handoff, while rejecting missing, wrong, cross-session, and replayed tokens", async () => {
    backendRequests.length = 0;

    const first = await fetch(`${origin()}/onboarding`, { headers: { Accept: "text/html" } });
    const firstToken = first.headers.get("x-csrf-token");
    const firstCookies = updateCookieJar(first);
    const firstHtml = await first.text();

    expect(first.status).toBe(200);
    expect(first.headers.get("cache-control")).toContain("no-store");
    expect(first.headers.get("access-control-allow-origin")).toBeNull();
    expect(firstToken).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(firstCookies).toContain("__onboarding_csrf=");
    // React Router may serialize loader data in a streamed compact format, so
    // the header is the stable public token contract before any form is shown.
    expect(firstHtml).toContain("Nzb DAV Setup");

    const handoff = await fetch(`${origin()}/onboarding`, {
      method: "POST",
      redirect: "manual",
      headers: {
        Origin: origin(),
        Cookie: firstCookies,
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: new URLSearchParams({
        action: "handoff",
        csrfToken: firstToken!,
        username: "operator",
        password: "credential-not-retained-in-test-output",
      }),
    });

    expect(handoff.status).toBe(302);
    expect(handoff.headers.get("location")).toBe("/onboarding?step=usenet");
    expect(handoff.headers.get("x-csrf-token")).toMatch(/^[A-Za-z0-9_-]+$/);
    const handoffCookies = updateCookieJar(handoff, firstCookies);
    expect(handoffCookies).toContain("__setup=");
    expect(handoffCookies).toContain("__session=");
    const handoffSetCookies = cookiesFrom(handoff);
    expect(handoffSetCookies).toHaveLength(3);
    expect(handoffSetCookies.map((cookie) => cookie.split("=", 1)[0]).sort())
      .toEqual(["__onboarding_csrf", "__session", "__setup"]);
    expect(handoffSetCookies).toEqual(expect.arrayContaining([
      expect.stringMatching(/^__session=[^;]+; Max-Age=604800; Path=\/; HttpOnly; SameSite=Strict$/),
      expect.stringMatching(/^__setup=[^;]+; Max-Age=900; Path=\/; HttpOnly; SameSite=Strict$/),
      expect.stringMatching(/^__onboarding_csrf=[^;]+; Max-Age=3600; Path=\/; HttpOnly; SameSite=Strict$/),
    ]));

    // A real document redirect must preserve all three independent cookies:
    // CSRF, server-side setup handle, and authenticated browser session.
    // Without the final cookie, the public handoff looks successful but the
    // first authenticated draft action is redirected to login.
    const provider = await fetch(`${origin()}/onboarding`, {
      method: "POST",
      redirect: "manual",
      headers: {
        Origin: origin(),
        Cookie: handoffCookies,
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: new URLSearchParams({
        action: "add-provider",
        csrfToken: handoff.headers.get("x-csrf-token")!,
        "provider-host": "provider.example",
        "provider-port": "563",
        "provider-user": "operator",
        "provider-pass": "credential-not-retained-in-test-output",
        "provider-max": "1",
        "provider-type": "1",
      }),
    });
    expect(provider.status).toBe(302);
    expect(provider.headers.get("location")).toBe("/onboarding?step=usenet");

    // The browser keeps its unchanged authenticated cookie across a normal
    // frontend/container restart. The restarted public proxy must accept it;
    // a new anonymous onboarding load or a fabricated cookie is not valid.
    await restartFrontend();
    const restartedStatus = await fetch(`${origin()}/api/setup/status`, {
      headers: { Cookie: handoffCookies },
    });
    expect(restartedStatus.status).toBe(200);
    const handoffsAfterValidPost = backendRequests.filter((entry) => entry.path === "/api/setup/handoff");
    expect(handoffsAfterValidPost).toHaveLength(1);
    expect(handoffsAfterValidPost[0]?.headers["x-api-key"]).toBe("onboarding-http-integration-backend-key-0123456789");
    expect(handoffsAfterValidPost[0]?.headers.cookie).toBeUndefined();
    expect(handoffsAfterValidPost[0]?.headers["x-csrf-token"]).toBeUndefined();

    // Alter the browser-issued opaque value in place; this is not a fabricated
    // cookie. The restarted production listener must fail closed on tampering.
    const tamperedCookies = handoffCookies.replace(/(__session=)([A-Za-z0-9_-])/, (_whole, prefix: string, first: string) =>
      `${prefix}${first === "A" ? "B" : "A"}`,
    );
    expect(tamperedCookies).not.toBe(handoffCookies);
    const tamperedStatus = await fetch(`${origin()}/api/setup/status`, {
      headers: { Cookie: tamperedCookies },
    });
    expect(tamperedStatus.status).toBe(401);

    // Logout revokes the matching registry record. Replaying the actual
    // pre-logout browser cookie (rather than the deletion response) remains
    // unauthorized; no session value is generated by this regression.
    const csrfRefresh = await fetch(`${origin()}/api/csrf-token`, { headers: { Cookie: handoffCookies } });
    expect(csrfRefresh.status).toBe(200);
    const cookiesBeforeLogout = updateCookieJar(csrfRefresh, handoffCookies);
    const logoutResponse = await fetch(`${origin()}/logout`, {
      method: "POST",
      redirect: "manual",
      headers: {
        Origin: origin(),
        Cookie: cookiesBeforeLogout,
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: new URLSearchParams({ csrfToken: csrfRefresh.headers.get("x-csrf-token")! }),
    });
    expect(logoutResponse.status).toBe(302);
    expect(logoutResponse.headers.get("location")).toBe("/login");
    const replayedAfterLogout = await fetch(`${origin()}/api/setup/status`, {
      headers: { Cookie: cookiesBeforeLogout },
    });
    expect(replayedAfterLogout.status).toBe(401);

    const second = await fetch(`${origin()}/onboarding`, { headers: { Accept: "text/html" } });
    const secondToken = second.headers.get("x-csrf-token");
    const secondCookies = updateCookieJar(second);
    expect(secondToken).toMatch(/^[A-Za-z0-9_-]+$/);

    const post = async (cookie: string, fields: URLSearchParams): Promise<Response> => fetch(`${origin()}/onboarding`, {
      method: "POST",
      redirect: "manual",
      headers: {
        Origin: origin(),
        Cookie: cookie,
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: fields,
    });

    const third = await fetch(`${origin()}/onboarding`, { headers: { Accept: "text/html" } });
    const thirdToken = third.headers.get("x-csrf-token");
    const thirdCookies = updateCookieJar(third);
    expect(thirdToken).toMatch(/^[A-Za-z0-9_-]+$/);

    for (const [cookie, fields] of [
      [secondCookies, new URLSearchParams({ action: "handoff", username: "operator", password: "not-output" })],
      [secondCookies, new URLSearchParams({ action: "handoff", csrfToken: "wrong-token", username: "operator", password: "not-output" })],
      [thirdCookies, new URLSearchParams({ action: "handoff", csrfToken: secondToken!, username: "operator", password: "not-output" })],
    ] as const) {
      const rejected = await post(cookie, fields);
      expect(rejected.status).toBe(200);
      expect(await rejected.text()).toContain("CSRF token is missing or invalid.");
    }

    expect(backendRequests.filter((entry) => entry.path === "/api/setup/handoff")).toHaveLength(1);
  }, 20_000);
});

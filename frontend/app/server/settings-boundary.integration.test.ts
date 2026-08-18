import http from "node:http";
import { once } from "node:events";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

const SECRET_MARKERS = [
  "api-key-secret",
  "webdav-password-secret",
  "arr-api-secret",
  "l2-secret",
  "usenet-password-secret",
];

let frontend: ChildProcessWithoutNullStreams;
let backend: http.Server;
let frontendPort = 0;
let backendPort = 0;
let backendPaths: string[] = [];

beforeAll(async () => {
  process.env.NODE_ENV = "test";
  process.env.FRONTEND_BACKEND_API_KEY = "frontend-backend-key";
  process.env.SESSION_KEY = "settings-boundary-test-session-key";
  process.env.PUBLIC_ORIGIN = "";

  backend = http.createServer(async (request, response) => {
    backendPaths.push(request.url || "");
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(Buffer.from(chunk));
    const path = new URL(request.url || "/", "http://backend.invalid").pathname;

    let payload: unknown;
    switch (path) {
      case "/api/is-onboarding":
        payload = { isOnboarding: false };
        break;
      case "/api/authenticate":
        payload = { authenticated: true };
        break;
      case "/api/admin-settings":
        payload = {
          config: {
            "api.key": "",
            "webdav.pass": "",
            "arr.instances": JSON.stringify({ RadarrInstances: [{ Host: "http://radarr:7878", HasApiKey: true }] }),
            "cache.l2.secret-key": "",
            "general.base-url": "sentinel-setting",
          },
          hasSecrets: { "api.key": true, "webdav.pass": true, "arr.instances": true, "cache.l2.secret-key": true },
        };
        break;
      case "/api/admin-settings/usenet":
        payload = {
          revision: "opaque-revision",
          providers: [{
            id: "provider-1",
            host: "news.example.com",
            port: 119,
            ssl: false,
            user: "agent",
            max: 5,
            type: 1,
            hasPassword: true,
            password: "usenet-password-secret",
          }],
        };
        break;
      case "/api/encryption-status":
        payload = {
          keySet: true,
          plaintextSecretsCount: 0,
          bannerSeverity: "none",
          migrationCompletedAt: null,
          postMigrationAcknowledged: true,
          postMigrationAcknowledgedAt: null,
        };
        break;
      default:
        response.statusCode = 404;
        response.end("not found");
        return;
    }

    const body = JSON.stringify(payload);
    response.setHeader("Content-Type", "application/json");
    response.end(body);
  });
  backend.listen(0, "127.0.0.1");
  await once(backend, "listening");
  backendPort = (backend.address() as { port: number }).port;
  process.env.BACKEND_URL = `http://127.0.0.1:${backendPort}`;

  const probe = http.createServer();
  probe.listen(0, "127.0.0.1");
  await once(probe, "listening");
  frontendPort = (probe.address() as { port: number }).port;
  probe.close();
  await once(probe, "close");

  frontend = spawn(process.execPath, ["node_modules/tsx/dist/cli.mjs", "server.js"], {
    cwd: process.cwd(),
    env: {
      ...process.env,
      NODE_ENV: "development",
      PORT: String(frontendPort),
      BACKEND_URL: `http://127.0.0.1:${backendPort}`,
      FRONTEND_BACKEND_API_KEY: "frontend-backend-key",
      SESSION_KEY: "settings-boundary-test-session-key",
      PUBLIC_ORIGIN: "",
    },
    stdio: ["pipe", "pipe", "pipe"],
  });
  await waitForFrontend(frontend);
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
}, 60_000);

const waitForFrontend = async (child: ChildProcessWithoutNullStreams): Promise<void> => {
  await new Promise<void>((resolve, reject) => {
    let output = "";
    const timer = setTimeout(() => reject(new Error(`frontend server did not start: ${output}`)), 30_000);
    const onData = (chunk: Buffer | string) => {
      output += chunk.toString();
      if (output.includes("Server is running on")) {
        clearTimeout(timer);
        resolve();
      }
    };
    child.stdout.on("data", onData);
    child.stderr.on("data", onData);
    child.once("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.once("exit", (code) => {
      if (code !== null) {
        clearTimeout(timer);
        reject(new Error(`frontend server exited (${code}): ${output}`));
      }
    });
  });
};

const origin = () => `http://127.0.0.1:${frontendPort}`;

const cookieJar = (response: Response, previous = ""): string => {
  const getSetCookie = (response.headers as Headers & { getSetCookie?: () => string[] }).getSetCookie;
  const values = (getSetCookie ? getSetCookie.call(response.headers) : undefined)
    || (response.headers.get("set-cookie") || "").split(/,(?=[^;]+=[^;]+)/g);
  const cookies = new Map(previous.split("; ").filter(Boolean).map(value => value.split("=", 1)[0]).map(name => [name, ""]));
  for (const value of values) {
    const pair = value.split(";", 1)[0];
    const name = pair.split("=", 1)[0];
    if (name) cookies.set(name, pair);
  }
  return [...cookies.values()].filter(Boolean).join("; ");
};

const csrfFromHtml = (html: string): string => {
  const match = html.match(/name="csrfToken"\s+value="([A-Za-z0-9_-]+)"/);
  if (!match) throw new Error("login page did not contain a CSRF token");
  return match[1];
};

describe("settings route authentication boundary", () => {
  it("rejects a direct single-fetch loader without a body or backend settings request", async () => {
    backendPaths = [];
    const response = await fetch(`${origin()}/settings.data?_routes=routes/settings`, { redirect: "manual" });
    const body = await response.text();

    // React Router's single-fetch protocol may wrap an empty route response
    // in a 202/401 data envelope; it must never become a settings payload.
    expect([202, 401]).toContain(response.status);
    for (const marker of SECRET_MARKERS) expect(body).not.toContain(marker);
    expect(backendPaths).not.toEqual(expect.arrayContaining([
      "/api/get-config",
      "/api/admin-settings",
      "/api/admin-settings/usenet",
      "/api/encryption-status",
    ]));
  });

  it("rejects unauthenticated queue, explore, and health single-fetch loaders without contacting the backend", async () => {
    for (const route of ["queue", "explore", "health"]) {
      backendPaths = [];
      const response = await fetch(`${origin()}/${route}.data?_routes=routes/${route}`, { redirect: "manual" });
      const body = await response.text();
      expect([202, 401, 302]).toContain(response.status);
      for (const marker of SECRET_MARKERS) expect(body).not.toContain(marker);
      expect(backendPaths).toEqual([]);
    }
  });

  it("serves settings only after a real login session is established", async () => {
    backendPaths = [];
    const loginPage = await fetch(`${origin()}/login`);
    const loginHtml = await loginPage.text();
    let cookies = cookieJar(loginPage);
    const csrfToken = csrfFromHtml(loginHtml);

    const login = await fetch(`${origin()}/login`, {
      method: "POST",
      redirect: "manual",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
        Origin: origin(),
        Host: `127.0.0.1:${frontendPort}`,
        Cookie: cookies,
      },
      body: new URLSearchParams({ csrfToken, username: "operator", password: "operator-password" }),
    });
    cookies = cookieJar(login, cookies);
    expect([301, 302, 303, 307, 308]).toContain(login.status);

    const response = await fetch(`${origin()}/settings.data?_routes=routes/settings`, {
      headers: { Cookie: cookies, Host: `127.0.0.1:${frontendPort}` },
    });
    const body = await response.text();

    expect(response.status).toBe(200);
    expect(body).toContain("sentinel-setting");
    for (const marker of SECRET_MARKERS) expect(body).not.toContain(marker);
    expect(backendPaths).toEqual(expect.arrayContaining([
      "/api/is-onboarding",
      "/api/authenticate",
      "/api/admin-settings",
      "/api/admin-settings/usenet",
      "/api/encryption-status",
    ]));
  });
});

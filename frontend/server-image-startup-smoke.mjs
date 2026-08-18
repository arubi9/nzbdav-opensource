import { execFileSync, spawn, spawnSync } from "node:child_process";
import net from "node:net";
import process from "node:process";
import { fileURLToPath } from "node:url";

const cwd = fileURLToPath(new URL(".", import.meta.url));
const ownedImage = !process.env.FRONTEND_IMAGE_SMOKE_IMAGE;
const image = process.env.FRONTEND_IMAGE_SMOKE_IMAGE || `nzb-webdav-frontend-smoke:${process.pid}`;
const containers = [];

const docker = (args, options = {}) => execFileSync("docker", args, {
  encoding: "utf8",
  stdio: "pipe",
  ...options,
}).trim();

function freePort() {
  return new Promise((resolve, reject) => {
    const probe = net.createServer();
    probe.once("error", reject);
    probe.listen(0, "127.0.0.1", () => {
      const address = probe.address();
      probe.close((error) => error ? reject(error) : resolve(address.port));
    });
  });
}

const cookiesFrom = (response) => typeof response.headers.getSetCookie === "function"
  ? response.headers.getSetCookie()
  : [response.headers.get("set-cookie") || ""];

function assertCookieMode(cookies, secure, label) {
  if (!cookies.some(Boolean)) throw new Error(`${label} did not issue a cookie`);
  const hasSecure = cookies.some((cookie) => /(?:^|;)\s*Secure(?:;|$)/i.test(cookie));
  if (hasSecure !== secure) {
    throw new Error(`${label} cookie Secure=${hasSecure}, expected ${secure}: ${cookies.join(" | ")}`);
  }
}

async function exercise(baseUrl, headers, secure, label) {
  const response = await fetch(`${baseUrl}/onboarding`, { headers });
  assertCookieMode(cookiesFrom(response), secure, label);
  if (response.status < 200 || response.status >= 500) {
    throw new Error(`${label} login returned unexpected HTTP ${response.status}`);
  }
}

async function waitForReady(baseUrl, headers, label) {
  const deadline = Date.now() + 15000;
  let lastError = "";
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${baseUrl}/onboarding`, { headers });
      if (response.status < 500) return;
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = String(error);
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`${label} did not become ready: ${lastError}`);
}

async function localImageContract() {
  const server = fileURLToPath(new URL("dist-node/server.js", import.meta.url));
  const start = async (environment, requestedPort) => {
    const port = requestedPort || await freePort();
    const child = spawn(process.execPath, [server], {
      cwd,
      env: { ...environment, PORT: String(port) },
      stdio: ["ignore", "pipe", "pipe"],
    });
    let output = "";
    child.stdout.on("data", (chunk) => { output += chunk; });
    child.stderr.on("data", (chunk) => { output += chunk; });
    const ready = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`${environment.PUBLIC_ORIGIN} startup timed out\n${output}`)), 10000);
      child.once("error", (error) => { clearTimeout(timer); reject(error); });
      child.stdout.on("data", () => {
        // The process intentionally listens on all container interfaces. The
        // host-side probe must use loopback, not the readiness log address.
        if (output.includes(`Server is running on http://0.0.0.0:${port}`)) {
          clearTimeout(timer);
          resolve(port);
        }
      });
      child.once("exit", (code, signal) => {
        if (code !== null || signal) {
          clearTimeout(timer);
          reject(new Error(`server exited before readiness (code ${code}, signal ${signal})\n${output}`));
        }
      });
    });
    await ready;
    return { child, port };
  };
  const stop = async ({ child }) => {
    child.kill();
    await new Promise((resolve) => child.once("exit", resolve));
  };
  const baseEnvironment = {
    NODE_ENV: "production",
    SESSION_KEY: "image-smoke-session-key-012345678901234567890123",
    FRONTEND_BACKEND_API_KEY: "image-smoke-api-key-012345678901234567890123",
    BACKEND_URL: "http://127.0.0.1:1",
  };

  const httpPort = await freePort();
  const httpServer = await start({ ...baseEnvironment, PUBLIC_ORIGIN: `http://127.0.0.1:${httpPort}`, SECURE_COOKIES: "false" }, httpPort);
  try {
    await exercise(`http://127.0.0.1:${httpServer.port}`, {}, false, "HTTP");
  } finally {
    await stop(httpServer);
  }

  const httpsPort = await freePort();
  const httpsOrigin = `https://127.0.0.1:${httpsPort}`;
  const httpsServer = await start({
    ...baseEnvironment,
    PUBLIC_ORIGIN: httpsOrigin,
    SECURE_COOKIES: "true",
    TRUSTED_PROXIES: "127.0.0.1",
  }, httpsPort);
  try {
    const forwarded = { Host: `127.0.0.1:${httpsPort}`, "X-Forwarded-Proto": "https" };
    await exercise(`http://127.0.0.1:${httpsServer.port}`, forwarded, true, "validated HTTPS PUBLIC_ORIGIN");
    const mismatch = await fetch(`http://127.0.0.1:${httpsServer.port}/onboarding`, {
      headers: { ...forwarded, "X-Forwarded-Host": `mismatch.invalid:${httpsPort}` },
    });
    if (mismatch.status !== 400) throw new Error(`mismatched origin returned HTTP ${mismatch.status}`);
  } finally {
    await stop(httpsServer);
  }
}

async function containerContract() {
  const run = async (environment, port, secure) => {
    const args = [
      "run", "--detach", "--publish", `127.0.0.1:${port}:3000`,
      "--env", "NODE_ENV=production",
      "--env", "SESSION_KEY=image-startup-smoke-session-key-012345678901234567890123",
      "--env", "FRONTEND_BACKEND_API_KEY=image-startup-smoke-api-key-012345678901234567890123",
      "--env", "BACKEND_URL=http://127.0.0.1:5000",
      "--env", `PUBLIC_ORIGIN=${environment.PUBLIC_ORIGIN}`,
    ];
    if (environment.TRUSTED_PROXIES) args.push("--env", `TRUSTED_PROXIES=${environment.TRUSTED_PROXIES}`);
    const container = docker([...args, image]);
    containers.push(container);
    const headers = secure
      ? { Host: `127.0.0.1:${port}`, "X-Forwarded-Proto": "https" }
      : {};
    const baseUrl = `http://127.0.0.1:${port}`;
    await waitForReady(baseUrl, headers, secure ? "HTTPS frontend container" : "HTTP frontend container");
    await exercise(baseUrl, headers, secure, secure ? "HTTPS frontend container" : "HTTP frontend container");
    return { baseUrl, headers };
  };

  const httpPort = await freePort();
  await run({ PUBLIC_ORIGIN: "" }, httpPort, false);
  const httpsPort = await freePort();
  const https = await run({ PUBLIC_ORIGIN: `https://127.0.0.1:${httpsPort}`, TRUSTED_PROXIES: "0.0.0.0/0" }, httpsPort, true);
  const mismatch = await fetch(`${https.baseUrl}/onboarding`, {
    headers: { ...https.headers, "X-Forwarded-Host": `mismatch.invalid:${httpsPort}` },
  });
  if (mismatch.status !== 400) throw new Error(`mismatched container origin returned HTTP ${mismatch.status}`);
}

try {
  if (process.env.FRONTEND_IMAGE_SMOKE_IN_BUILD === "1") {
    await localImageContract();
    console.log("frontend build image contract smoke passed");
  } else {
    if (ownedImage) docker(["build", "--tag", image, "."], { cwd });
    await containerContract();
    console.log("frontend container HTTP/HTTPS cookie and origin smoke passed");
  }
} finally {
  for (const container of containers) spawnSync("docker", ["rm", "--force", container], { stdio: "ignore" });
  if (ownedImage) spawnSync("docker", ["image", "rm", "--force", image], { stdio: "ignore" });
}

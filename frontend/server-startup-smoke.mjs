import { spawn } from "node:child_process";
import net from "node:net";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";

const cwd = fileURLToPath(new URL(".", import.meta.url));
const server = fileURLToPath(new URL("dist-node/server.js", import.meta.url));

// Docker runs build:server immediately before this check. Standalone use therefore
// requires `npm run build:server` first; this keeps the smoke test from compiling twice.
if (!existsSync(server)) {
  throw new Error("dist-node/server.js is missing; run npm run build:server first");
}

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

async function runAttempt(port) {
  // Keep the child environment explicit: only these settings plus the host
  // runtime paths are permitted; deployment secrets cannot leak into the test.
  const env = {
    NODE_ENV: "production",
    PORT: String(port),
    SESSION_KEY: "startup-smoke-session-key-0123456789012345",
    FRONTEND_BACKEND_API_KEY: "startup-smoke-api-key-0123456789012345",
    BACKEND_URL: "http://127.0.0.1:5000",
    PUBLIC_ORIGIN: "http://127.0.0.1",
  };
  if (process.env.PATH) env.PATH = process.env.PATH;
  if (process.env.SystemRoot) env.SystemRoot = process.env.SystemRoot;
  const child = spawn(process.execPath, [server], {
    cwd,
    env,
    stdio: ["ignore", "pipe", "pipe"],
  });
  let output = "";
  child.stdout.on("data", (chunk) => { output += chunk; });
  child.stderr.on("data", (chunk) => { output += chunk; });

  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (callback) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      callback();
    };
    const timer = setTimeout(() => {
      child.kill();
      finish(() => reject(new Error(`server did not report readiness within 10 seconds\n${output}`)));
    }, 10000);
    child.once("error", (error) => finish(() => reject(error)));
    child.once("exit", (code, signal) => {
      if (!settled) {
        finish(() => reject(new Error(`server exited before startup (code ${code}, signal ${signal})\n${output}`)));
      }
    });
    child.stdout.on("data", () => {
      if (output.includes(`Server is running on http://0.0.0.0:${port}`)) {
        child.kill();
        finish(() => resolve(output));
      }
    });
  });
}

let output;
for (let attempt = 0; attempt < 5; attempt += 1) {
  const port = await freePort();
  try {
    output = await runAttempt(port);
    break;
  } catch (error) {
    if (!String(error?.message ?? error).includes("EADDRINUSE") || attempt === 4) throw error;
  }
}

if (!output) throw new Error("server startup smoke did not complete");
console.log("server startup smoke passed");

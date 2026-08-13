import { spawn } from "node:child_process";
import process from "node:process";
import { fileURLToPath } from "node:url";
import { dirname } from "node:path";

// Port 0 is atomically allocated by the OS, avoiding collisions between builds.
const child = spawn(process.execPath, ["dist-node/server.js"], {
  cwd: dirname(fileURLToPath(import.meta.url)),
  env: {
    NODE_ENV: "production",
    PORT: "0",
    BACKEND_URL: "http://127.0.0.1:1",
    FRONTEND_BACKEND_API_KEY: "",
  },
  stdio: ["ignore", "pipe", "pipe"],
});

let output = "";
const capture = (chunk) => {
  output += chunk.toString();
  if (output.includes("Server is running on")) finish(0);
};
child.stdout.on("data", capture);
child.stderr.on("data", (chunk) => { output += chunk.toString(); });

const timer = setTimeout(() => finish(1), 10000);
let done = false;
function finish(code) {
  if (done) return;
  done = true;
  clearTimeout(timer);
  child.kill();
  if (code !== 0) process.stderr.write(output);
  process.exit(code);
}
child.once("error", (error) => {
  output += `${error}\n`;
  finish(1);
});
child.once("exit", (code) => {
  if (!done && code !== 0) finish(1);
});

import compression from "compression";
import express from "express";
import morgan from "morgan";
import http from "http";
import { WebSocketServer } from "ws";
import { isTrustedProxyAddress, validateStartupConfig } from "./server-config.js";

// Validate all startup configuration before creating/listening on a server.
// Production never receives generated secret/API-key fallbacks.
const startupConfig = validateStartupConfig(process.env);

// Short-circuit the type-checking of the built output.
const BUILD_PATH = "../build/server/index.js";
const DEVELOPMENT = process.env.NODE_ENV === "development";
const PORT = startupConfig.port;
if (startupConfig.insecureDevCookies && !startupConfig.secureCookies) {
  console.warn("WARNING: non-loopback HTTP is using explicitly acknowledged insecure development cookies; use HTTPS with Secure cookies in production.");
}

// Initialize the express app
const app = express();
app.set("trust proxy", (address: string) => isTrustedProxyAddress(address));
app.use(
  compression({
    // Don't compress proxied WebDAV/media/API responses; keep Content-Length intact for seek
    filter: (req, res) => {
      const path = req.path || "";
      if (
        path.startsWith("/view") ||
        path.startsWith("/.ids") ||
        path.startsWith("/nzbs") ||
        path.startsWith("/content") ||
        path.startsWith("/completed-symlinks") ||
        path.startsWith("/api")
      ) {
        return false;
      }
      return compression.filter(req, res);
    },
  }),
);
app.disable("x-powered-by");

// Initialize the websocket server as soon as both it and the server-module are ready
let _serverModule: any = null;
let _websocketServer: WebSocketServer | null = null;
const setWebsocketServer = (websocketServer: WebSocketServer) => {
  if (_websocketServer != null) return;
  if (_serverModule != null) _serverModule.initializeWebsocketServer(websocketServer);
  _websocketServer = websocketServer;
}
const setServerModule = (serverModule: any) => {
  if (_serverModule != null) return;
  if (_websocketServer != null) serverModule.initializeWebsocketServer(_websocketServer);
  _serverModule = serverModule;
}

// Handle development vs production
if (DEVELOPMENT) {
  console.log("Starting development server");
  const viteDevServer = await import("vite").then((vite) =>
    vite.createServer({
      server: { middlewareMode: true },
    }),
  );
  app.use(viteDevServer.middlewares);
  app.use(async (req, res, next) => {
    try {
      const serverModule = await viteDevServer.ssrLoadModule("./server/app.ts");
      setServerModule(serverModule);
      return await serverModule.app(req, res, next);
    } catch (error) {
      if (typeof error === "object" && error instanceof Error) {
        viteDevServer.ssrFixStacktrace(error);
      }
      next(error);
    }
  });
} else {
  console.log("Starting production server");
  app.use(
    "/assets",
    express.static("build/client/assets", { immutable: true, maxAge: "1y" }),
  );
  morgan.token("pathname", (req) => {
    try { return new URL((req as express.Request).originalUrl || req.url || "/", "http://localhost").pathname; }
    catch { return "/"; }
  });
  app.use(morgan(":method :pathname :status :res[content-length] - :response-time ms", {
    skip: (req, res) => res.statusCode < 400 || (req.path || req.url) === "/favicon.ico",
  }));
  app.use(express.static("build/client", { maxAge: "1h" }));
  const serverModule = await import(BUILD_PATH);
  app.use(serverModule.app);
  setServerModule(serverModule);
}

// Create both the http and websocket servers
const server = http.createServer(app);
// Bound every socket and header phase; route-level readers add their own
// bounded body deadline for stalled chunked requests.
server.requestTimeout = Math.min(startupConfig.backendTimeoutMs, 120_000);
server.headersTimeout = Math.min(server.requestTimeout, 30_000);
server.keepAliveTimeout = Math.min(server.requestTimeout, 10_000);
setWebsocketServer(new WebSocketServer({ server }));

// Begin listening for connections
server.listen(PORT, startupConfig.listenAddress, () => {
  console.log(`Server is running on http://${startupConfig.listenAddress}:${PORT}`);
});

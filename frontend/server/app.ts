import "react-router";
import { createRequestHandler } from "@react-router/express";
import express from "express";
import { createHmac } from "node:crypto";
import type { ServerResponse } from "node:http";
import { createProxyMiddleware } from "http-proxy-middleware";
import { websocketServer } from "./websocket.server";
import { isAuthenticated } from "~/auth/authentication.server";
import { getCsrfToken, validateCsrfToken } from "~/onboarding/onboarding-csrf.server";
import { externalOriginFromHeaders, isTrustedProxyAddress, validateCookieConfig } from "../server-config";

declare module "react-router" {
  interface AppLoadContext {
    VALUE_FROM_EXPRESS: string;
  }
}

export const app = express();
export const initializeWebsocketServer = websocketServer.initialize;
const cookieConfig = validateCookieConfig();

// Express must only trust a socket peer explicitly listed by the operator. A
// browser cannot opt into proxy semantics by supplying X-Forwarded headers.
app.set("trust proxy", (address: string) => isTrustedProxyAddress(address));

const API_BODY_LIMIT = 8 * 1024 * 1024;
const UPLOAD_BODY_LIMIT = Number.parseInt(
  process.env.FRONTEND_MAX_UPLOAD_BYTES || process.env.MAX_UPLOAD_BYTES || process.env.UPLOAD_MAX_BYTES || "104857600",
  10,
);
const configuredRequestTimeout = Number.parseInt(process.env.FRONTEND_PROXY_TIMEOUT_MS || "120000", 10);
const REQUEST_TIMEOUT_MS = Number.isSafeInteger(configuredRequestTimeout) && configuredRequestTimeout > 0
  ? Math.min(configuredRequestTimeout, 120_000)
  : 120_000;
const uploadPath = (req: express.Request): boolean =>
  req.path === "/api" && req.query.mode === "addfile";

const isExpressResponse = (res: ServerResponse): res is express.Response => "locals" in res;

// Proxy all webdav and api requests to the backend.  The proxy does not follow
// redirects; browser redirects must be produced by the corresponding route.
const forwardToBackend = createProxyMiddleware({
  target: process.env.BACKEND_URL || "http://localhost:5000",
  changeOrigin: true,
  followRedirects: false,
  on: {
    proxyRes: (proxyRes, _req, res) => {
      // Backend credentials, cookies, CORS policy, and redirects are not part
      // of the browser-facing frontend contract.
      for (const header of [
        "set-cookie", "www-authenticate", "proxy-authenticate", "authorization", "proxy-authorization",
        "x-api-key", "x-setup-grant", "x-csrf-token",
      ]) delete proxyRes.headers[header];
      for (const header of Object.keys(proxyRes.headers)) {
        if (header.toLowerCase().startsWith("access-control-") || header.toLowerCase() === "location") {
          delete proxyRes.headers[header];
        }
      }
      if (isExpressResponse(res)) {
        const csrfHeaders = res.locals.csrfHeaders as HeadersInit | undefined;
        if (csrfHeaders) {
          for (const [key, value] of new Headers(csrfHeaders).entries()) {
            res.setHeader(key, value);
          }
        }
      }
      proxyRes.on("close", () => {
        if (!res.writableEnded) res.end();
      });
    },
  },
});

const isProxyPath = (req: express.Request): boolean =>
  req.path.startsWith("/api") || req.path.startsWith("/view") || req.path.startsWith("/nzbs") ||
  req.path.startsWith("/content") || req.path.startsWith("/completed-symlinks") || req.path.startsWith("/.ids");

const normalizeClientSource = (value: string | undefined): string => {
  const normalized = (value || "unknown").trim().replace(/^::ffff:/i, "").replace(/^\[|\]$/g, "");
  return normalized && normalized.length <= 128 ? normalized : "unknown";
};

export const opaqueClientSourceFor = (source: string, key?: string): string => {
  const sourceKey = key || process.env.FRONTEND_BACKEND_API_KEY || process.env.SESSION_KEY;
  if (!sourceKey) throw new Error("A configured frontend secret is required for client-source binding.");
  return createHmac("sha256", sourceKey).update(normalizeClientSource(source)).digest("base64url");
};

const trustedClientSourceFor = (req: express.Request): string =>
  opaqueClientSourceFor(normalizeClientSource(req.ip || req.socket.remoteAddress));

const stripBrowserCredentials = (req: express.Request): void => {
  const opaqueSource = typeof req.headers["x-frontend-client-source"] === "string"
    ? req.headers["x-frontend-client-source"]
    : trustedClientSourceFor(req);
  for (const header of [
    "cookie", "authorization", "proxy-authorization", "www-authenticate", "proxy-authenticate",
    "x-setup-grant", "x-csrf-token", "x-api-key", "forwarded", "x-forwarded-for", "x-forwarded-host",
    "x-forwarded-proto", "x-forwarded-port", "x-real-ip", "x-frontend-client-source",
  ]) delete req.headers[header];
  // Query credentials are just as ambient as credential headers. Keep normal
  // API parameters (cat, priority, etc.) while removing backend auth material.
  try {
    const url = new URL(req.url, "http://frontend.invalid");
    for (const key of ["apikey", "apiKey", "x-api-key", "token", "grant", "setup-grant", "setupGrant"]) {
      url.searchParams.delete(key);
    }
    req.url = `${url.pathname}${url.search}`;
  } catch {
    req.url = req.path;
  }
  // This is inserted after all ambient identity headers have been removed.
  // It is an opaque HMAC over the source selected by Express' validated trust
  // proxy configuration, never a browser-controlled IP/header value.
  req.headers["x-frontend-client-source"] = opaqueSource;
  req.headers["x-api-key"] = process.env.FRONTEND_BACKEND_API_KEY || "";
};

/** Return the exact external origin represented by this request. */
export const externalRequestOrigin = (req: express.Request): string =>
  externalOriginFromHeaders(req.headers, req.socket.remoteAddress, process.env, Boolean((req.socket as { encrypted?: boolean }).encrypted));

const safeRequestUrl = (req: express.Request): string => `${externalRequestOrigin(req)}${req.originalUrl}`;

const sanitizedRouteHeaders = (req: express.Request): Headers => {
  const headers = new Headers();
  const ambientIdentityHeaders = new Set([
    "forwarded", "x-forwarded-for", "x-forwarded-host", "x-forwarded-proto", "x-forwarded-port", "x-real-ip",
    "x-frontend-client-source",
  ]);
  for (const [key, value] of Object.entries(req.headers)) {
    if (ambientIdentityHeaders.has(key.toLowerCase())) continue;
    if (typeof value === "string") headers.set(key, value);
  }
  headers.set("host", new URL(externalRequestOrigin(req)).host);
  const source = req.headers["x-frontend-client-source"];
  if (typeof source === "string") headers.set("x-frontend-client-source", source);
  return headers;
};

const isMutation = (method: string): boolean =>
  !["GET", "HEAD", "OPTIONS", "PROPFIND"].includes(method);

const rejectPayload = (res: express.Response, status: number, error: string) =>
  res.status(status).json({ error });

const bodyLimitFor = (req: express.Request): number =>
  uploadPath(req) && Number.isFinite(UPLOAD_BODY_LIMIT) && UPLOAD_BODY_LIMIT > 0
    ? UPLOAD_BODY_LIMIT
    : API_BODY_LIMIT;

/**
 * Check the length and install a streaming counter.  This must be called only
 * after the asynchronous checks below: adding a data listener puts an
 * IncomingMessage into flowing mode and would otherwise race those checks.
 * The proxy remains responsible for consuming/forwarding the request body.
 */
const enforceStreamingBounds = (req: express.Request, res: express.Response): boolean => {
  const limit = bodyLimitFor(req);
  const contentLength = req.headers["content-length"];
  if (contentLength !== undefined && (!/^\d+$/.test(String(contentLength)) || Number(contentLength) > limit)) {
    rejectPayload(res, 413, "Request payload is too large.");
    return false;
  }

  req.setTimeout(REQUEST_TIMEOUT_MS, () => {
    if (!res.headersSent) rejectPayload(res, 408, "Request timed out.");
    req.destroy();
  });

  let received = 0;
  let rejected = false;
  req.on("data", (chunk: Buffer | string) => {
    if (rejected) return;
    received += Buffer.byteLength(chunk);
    if (received > limit) {
      rejected = true;
      if (!res.headersSent) rejectPayload(res, 413, "Request payload is too large.");
      req.destroy();
    }
  });
  // Adding the listener resumes an IncomingMessage. Pause it again so HPM's
  // synchronous pipe setup, immediately following this call, cannot miss data.
  req.pause();
  return true;
};

// Reject proxy identity headers before any route can use req.protocol/host.
app.use((req, res, next) => {
  try {
    externalRequestOrigin(req);
    // Inject the same opaque source into React Router actions. The inbound
    // header is overwritten before any route code can inspect it.
    req.headers["x-frontend-client-source"] = trustedClientSourceFor(req);
    next();
  } catch {
    rejectPayload(res, 400, "Invalid request origin.");
  }
});

// This endpoint is deliberately a GET and is only a token refresh mechanism;
// it never reaches the backend.
app.get("/api/csrf-token", async (req, res) => {
  res.set({ "Cache-Control": "no-store", Pragma: "no-cache", Expires: "0" });
  if (!await isAuthenticated(req)) return rejectPayload(res, 401, "Authentication required.");
  try {
    const requestHeaders = sanitizedRouteHeaders(req);
    const csrf = await getCsrfToken(new Request(`${safeRequestUrl(req)}`, {
      method: "GET",
      headers: requestHeaders,
    }));
    if (csrf.headers) {
      for (const [key, value] of new Headers(csrf.headers).entries()) res.setHeader(key, value);
    }
    res.setHeader("X-CSRF-Token", csrf.token);
    return res.status(200).json({ ok: true });
  } catch {
    return rejectPayload(res, 500, "Unable to refresh CSRF token.");
  }
});

app.use(async (req, res, next) => {
  const method = req.method.toUpperCase();
  const proxyPath = isProxyPath(req);

  if (req.path.startsWith("/.ids") && !["GET", "HEAD"].includes(method)) {
    return rejectPayload(res, 405, "Method not allowed.");
  }

  if (isMutation(method) && proxyPath) {
    // Account creation and authentication are called by protected React Router
    // actions.  They must never be exposed as public backend proxy endpoints.
    if (req.path === "/api/create-account" || req.path === "/api/authenticate") {
      return rejectPayload(res, 404, "Not found.");
    }

    // Do not install a data listener until all async checks have completed.
    // IncomingMessage starts flowing as soon as that listener is installed;
    // doing it before these awaits can lose the body before HPM subscribes.
    // Keep the request timeout active while authentication/CSRF are pending.
    req.setTimeout(REQUEST_TIMEOUT_MS, () => {
      if (!res.headersSent) rejectPayload(res, 408, "Request timed out.");
      req.destroy();
    });
    let authenticated = false;
    try {
      authenticated = await isAuthenticated(req);
    } catch {
      if (!req.destroyed && !res.headersSent) rejectPayload(res, 401, "Authentication required.");
      return;
    }
    if (req.destroyed || req.aborted) return;
    if (!authenticated) {
      rejectPayload(res, 401, "Authentication required.");
      return;
    }

    const forwardedHeaders = sanitizedRouteHeaders(req);
    const requestUrl = safeRequestUrl(req);
    const applyCorsSafeHeaders = async (): Promise<void> => {
      try {
        const csrf = await getCsrfToken(new Request(requestUrl, { method: "GET", headers: forwardedHeaders }));
        const headers = new Headers(csrf.headers || {});
        if (csrf.token) headers.set("X-CSRF-Token", csrf.token);
        for (const [key, value] of headers.entries()) {
          res.setHeader(key, value);
        }
      } catch {
        // ignore: fallback to the default 403 payload only.
      }
    };

    const token = String(req.headers["x-csrf-token"] || "");
    if (!token) {
      await applyCorsSafeHeaders();
      rejectPayload(res, 403, "CSRF validation failed.");
      return;
    }

    try {
      const csrfResult = await validateCsrfToken(
        new Request(requestUrl, { method, headers: forwardedHeaders }),
        new URLSearchParams({ csrfToken: token }),
      );
      if (csrfResult.headers) {
        res.locals.csrfHeaders = csrfResult.headers;
        for (const [key, value] of new Headers(csrfResult.headers).entries()) res.setHeader(key, value);
      }
    } catch {
      // Do not invoke the proxy on validation failure.  In particular, never
      // leak a CSRF token or the frontend session cookie to the backend.
      await applyCorsSafeHeaders();
      if (!req.destroyed && !res.headersSent) rejectPayload(res, 403, "CSRF validation failed.");
      return;
    }
    if (req.destroyed || req.aborted || !enforceStreamingBounds(req, res)) return;

    // The backend is authenticated with its private API key, not the browser
    // session.  Never forward browser credentials or setup material.
    stripBrowserCredentials(req);
    // The counter and proxy are deliberately adjacent and synchronous: once
    // the request is allowed to flow, HPM is already attached to it.
    return forwardToBackend(req, res, next);
  }

  if (method === "PROPFIND" || method === "OPTIONS" || proxyPath) {
    // Authenticate while the browser session cookie is still present. Reads do
    // not need CSRF, but unauthenticated reads must not become a backend oracle.
    let authenticated = false;
    try {
      authenticated = await isAuthenticated(req);
    } catch {
      return rejectPayload(res, 401, "Authentication required.");
    }
    if (!authenticated) return rejectPayload(res, 401, "Authentication required.");
    stripBrowserCredentials(req);
    return forwardToBackend(req, res, next);
  }
  next();
});

app.use(
  createRequestHandler({
    build: () => import("virtual:react-router/server-build"),
    getLoadContext() {
      return { VALUE_FROM_EXPRESS: "Hello from Express" };
    },
  }),
);

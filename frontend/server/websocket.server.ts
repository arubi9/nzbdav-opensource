import WebSocket, { WebSocketServer } from "ws";
import * as authentication from "../app/auth/authentication.server";
import type { AuthenticatedSession } from "../app/auth/authentication.server";
import type { IncomingMessage } from "http";
import { externalOriginFromHeaders, strictHttpOrigin, type StartupEnvironment } from "../server-config.js";

// Keep this list deliberately finite. These are the topics used by the frontend;
// topic names are not an API for arbitrary backend event routing.
export const WEBSOCKET_TOPICS = new Set([
    "qs", "qp", "qa", "qr", "ha", "hr", "hs", "hp", "cxs", "st2sy", "ctp",
]);
const MAX_SUBSCRIPTIONS_PER_CONNECTION = WEBSOCKET_TOPICS.size;
const MAX_CONNECTIONS = 256;
const MAX_TOTAL_SUBSCRIPTIONS = MAX_CONNECTIONS * MAX_SUBSCRIPTIONS_PER_CONNECTION;
const MAX_MESSAGES_PER_SECOND = 4;
export const WEBSOCKET_MAX_PAYLOAD = 64 * 1024;
export const MAX_BACKEND_WEBSOCKET_PAYLOAD_BYTES = WEBSOCKET_MAX_PAYLOAD;

type OriginRequest = Pick<IncomingMessage, "headers" | "socket">;

function singleHeader(value: string | string[] | undefined): string | undefined {
    return typeof value === "string" && value.length > 0 ? value : undefined;
}

function requestOrigin(request: OriginRequest, env: StartupEnvironment): string | undefined {
    try {
        return externalOriginFromHeaders(
            request.headers,
            request.socket.remoteAddress,
            env,
            Boolean((request.socket as { encrypted?: boolean }).encrypted),
        );
    } catch {
        return undefined;
    }
}

const normalizeBackendMessage = (rawMessage: string): { topic: string; message: string } | undefined => {
    let topicMessage: unknown;
    try {
        topicMessage = JSON.parse(rawMessage);
    } catch {
        return undefined;
    }

    if (typeof topicMessage !== "object" || topicMessage === null || Array.isArray(topicMessage)) return undefined;
    const keys = Object.keys(topicMessage);
    if (keys.length !== 2 || !Object.prototype.hasOwnProperty.call(topicMessage, "Topic") || !Object.prototype.hasOwnProperty.call(topicMessage, "Message")) return undefined;
    const untypedMessage = topicMessage as { [key: string]: unknown };

    const topic = typeof untypedMessage.Topic === "string" ? untypedMessage.Topic : "";
    if (!topic || !WEBSOCKET_TOPICS.has(topic)) return undefined;
    if (typeof untypedMessage.Message !== "string") return undefined;

    return {
        topic,
        message: JSON.stringify({ Topic: topic, Message: untypedMessage.Message }),
    };
};

/** Validate the browser Origin independently of cookies and authentication. */
export function isAllowedWebsocketOrigin(
    request: OriginRequest,
    origin: string | string[] | undefined,
    env: StartupEnvironment = process.env,
): boolean {
    const supplied = singleHeader(origin);
    if (!supplied || supplied.trim() !== supplied || supplied.toLowerCase() === "null") return false;
    let normalized: string;
    try {
        // Origin is an authority, not a URL. In particular, do not normalize
        // away a path slash or accept userinfo/query/fragment/control bytes.
        normalized = strictHttpOrigin("Origin", supplied);
    } catch {
        return false;
    }

    // Validate proxy authority even when PUBLIC_ORIGIN is configured. A
    // configured destination must not make duplicate/conflicting forwarded
    // headers disappear from the trust boundary.
    const configured = env.PUBLIC_ORIGIN?.trim();
    const expected = requestOrigin(request, env);
    if (configured) {
        try {
            // A direct listener has no proxy authority to validate; the
            // configured origin is authoritative. Forwarded authority must
            // still resolve successfully and agree with that origin.
            const hasForwarded = ["forwarded", "x-forwarded-host", "x-forwarded-proto", "x-forwarded-port"]
                .some((header) => request.headers[header] !== undefined);
            return normalized === strictHttpOrigin("PUBLIC_ORIGIN", configured)
                && (expected !== undefined || !hasForwarded)
                && (expected === undefined || normalized === expected);
        } catch { return false; }
    }
    return expected !== undefined && normalized === expected;
}

function initializeWebsocketServer(wss: WebSocketServer) {
    const subscriptions = new Map<string, Set<WebSocket>>();
    const lastMessage = new Map<string, string>();
    const connections = new Set<WebSocket>();
    let totalSubscriptions = 0;
    let backendClientStarted = false;

    const removeConnection = (ws: WebSocket) => {
        const state = ws as WebSocket & {
            nzbdavTopics?: Set<string>;
            nzbdavSessionId?: string;
            nzbdavSessionRevision?: number;
            nzbdavSessionExpiresAt?: number;
            nzbdavSessionUnsubscribe?: () => void;
            nzbdavExpiryTimer?: NodeJS.Timeout;
            nzbdavCloseTimer?: NodeJS.Timeout;
        };
        if (!connections.delete(ws)) return;
        state.nzbdavSessionUnsubscribe?.();
        state.nzbdavSessionUnsubscribe = undefined;
        if (state.nzbdavExpiryTimer) clearTimeout(state.nzbdavExpiryTimer);
        if (state.nzbdavCloseTimer) clearTimeout(state.nzbdavCloseTimer);
        state.nzbdavExpiryTimer = undefined;
        state.nzbdavCloseTimer = undefined;
        delete state.nzbdavSessionId;
        delete state.nzbdavSessionRevision;
        delete state.nzbdavSessionExpiresAt;
        const topics = state.nzbdavTopics;
        if (!topics) return;
        for (const topic of topics) {
            const clients = subscriptions.get(topic);
            if (clients?.delete(ws)) {
                totalSubscriptions--;
                if (clients.size === 0) subscriptions.delete(topic);
            }
        }
        topics.clear();
        delete state.nzbdavTopics;
    };

    wss.on("connection", async (ws: WebSocket, request: IncomingMessage) => {
        const state = ws as WebSocket & {
            nzbdavTopics?: Set<string>;
            nzbdavMessageTimes?: number[];
            nzbdavSessionId?: string;
            nzbdavSessionRevision?: number;
            nzbdavSessionExpiresAt?: number;
            nzbdavSessionUnsubscribe?: () => void;
            nzbdavExpiryTimer?: NodeJS.Timeout;
            nzbdavCloseTimer?: NodeJS.Timeout;
        };
        const closeForSession = (reason: string): void => {
            if (ws.readyState !== WebSocket.OPEN) return;
            ws.close(1008, reason);
            // A peer that stops reading must not retain a revoked subscription
            // forever. CLOSING is already excluded from event fanout; this is
            // only the bounded transport cleanup fallback.
            state.nzbdavCloseTimer = setTimeout(() => {
                if (ws.readyState !== WebSocket.CLOSED) ws.terminate();
            }, 1000);
            state.nzbdavCloseTimer.unref?.();
        };

        try {
            // The metadata lookup is deliberately paired with authentication.
            // The fallback keeps isolated unit harnesses that mock only
            // isAuthenticated safe; production always uses the session store.
            let session: AuthenticatedSession | undefined;
            // Some focused test mocks intentionally expose only the legacy
            // boolean auth function. Vitest throws when a missing mocked
            // export is read, so probe the optional API without propagating
            // that harness detail as a websocket 1011.
            let sessionLookup: typeof authentication.getAuthenticatedSession | undefined;
            try { sessionLookup = authentication.getAuthenticatedSession; } catch { sessionLookup = undefined; }
            if (typeof sessionLookup === "function") {
                const authenticatedSession = await sessionLookup(request);
                if (!authenticatedSession) {
                    ws.close(1008, "Unauthorized");
                    return;
                }
                session = authenticatedSession;
            } else if (!await authentication.isAuthenticated(request)) {
                ws.close(1008, "Unauthorized");
                return;
            }

            if (connections.size >= MAX_CONNECTIONS) {
                ws.close(1013, "Too many connections");
                return;
            }

            state.nzbdavTopics = new Set();
            state.nzbdavMessageTimes = [];
            const cleanup = () => removeConnection(ws);
            ws.on("close", cleanup);
            ws.on("error", cleanup);
            connections.add(ws);

            if (session) {
                state.nzbdavSessionId = session.id;
                state.nzbdavSessionRevision = session.revision;
                state.nzbdavSessionExpiresAt = session.expiresAt;
                // Logout, replacement, bounded-map eviction, revoke-all, and
                // store-observed expiry all invoke this callback immediately.
                state.nzbdavSessionUnsubscribe = session.subscribe(() => closeForSession("Session revoked"));
                if (Number.isFinite(session.expiresAt)) {
                    const delay = Math.max(0, session.expiresAt - Date.now());
                    state.nzbdavExpiryTimer = setTimeout(() => closeForSession("Session expired"), delay);
                    state.nzbdavExpiryTimer.unref?.();
                }
            }

            // Do not create an outbound backend session for unauthenticated
            // probes. It also avoids a reconnect loop when no browser is using
            // live updates.
            if (!backendClientStarted) {
                backendClientStarted = true;
                initializeWebsocketClient(subscriptions, lastMessage, connections);
            }

            ws.onmessage = (event: WebSocket.MessageEvent) => {
                try {
                    const now = Date.now();
                    const times = state.nzbdavMessageTimes!;
                    state.nzbdavMessageTimes = times.filter(time => now - time < 1000);
                    if (state.nzbdavMessageTimes.length >= MAX_MESSAGES_PER_SECOND) {
                        ws.close(1008, "Subscription rate exceeded");
                        return;
                    }
                    state.nzbdavMessageTimes.push(now);

                    const topics = JSON.parse(event.data.toString()) as unknown;
                    if (!topics || typeof topics !== "object" || Array.isArray(topics)) throw new Error("invalid topics");
                    const entries = Object.entries(topics as Record<string, unknown>);
                    if (entries.length > MAX_SUBSCRIPTIONS_PER_CONNECTION ||
                        entries.some(([topic, mode]) => !WEBSOCKET_TOPICS.has(topic) || (mode !== "state" && mode !== "event"))) {
                        throw new Error("invalid topics");
                    }
                    const nextTopics = new Set(entries.map(([topic]) => topic));
                    const currentTopics = state.nzbdavTopics!;
                    const added = [...nextTopics].filter(topic => !currentTopics.has(topic)).length;
                    const removed = [...currentTopics].filter(topic => !nextTopics.has(topic)).length;
                    if (totalSubscriptions - removed + added > MAX_TOTAL_SUBSCRIPTIONS) {
                        ws.close(1013, "Subscription capacity exceeded");
                        return;
                    }
                    for (const topic of currentTopics) {
                        if (nextTopics.has(topic)) continue;
                        const clients = subscriptions.get(topic);
                        if (clients?.delete(ws)) {
                            totalSubscriptions--;
                            if (clients.size === 0) subscriptions.delete(topic);
                        }
                    }
                    for (const [topic, mode] of entries) {
                        if (currentTopics.has(topic)) continue;
                        let clients = subscriptions.get(topic);
                        if (!clients) subscriptions.set(topic, clients = new Set());
                        clients.add(ws);
                        totalSubscriptions++;
                        if (mode === "state") {
                            const message = lastMessage.get(topic);
                            if (message && ws.readyState === WebSocket.OPEN) ws.send(message);
                        }
                    }
                    state.nzbdavTopics = nextTopics;
                } catch {
                    ws.close(1003, "Could not process topic subscription. If recently updated, try refreshing the page.");
                }
            };
        } catch (error) {
            console.error("Error authenticating websocket session:", error);
            ws.close(1011, "Internal server error");
        }
    });
}

export function initializeWebsocketClient(subscriptions: Map<string, Set<WebSocket>>, lastMessage: Map<string, string>, activeClients: Set<WebSocket>) {
    let reconnectRetryDelay = 1000;
    let reconnectTimeout: NodeJS.Timeout | null = null;
    const backendApiKey = process.env.FRONTEND_BACKEND_API_KEY;
    const url = getBackendWebsocketUrl();

    function closeAllFrontendClients() {
        for (const client of activeClients) {
            if (client.readyState === WebSocket.OPEN) client.close(1003, "Backend websocket contract violated.");
        }
    }

    function connect() {
        if (!backendApiKey || !url) {
            // Startup validation rejects this configuration for a real server;
            // these guards keep isolated import/test harnesses from turning a
            // missing key/URL into an asynchronous throw.
            console.error(!backendApiKey
                ? "FRONTEND_BACKEND_API_KEY is not configured; backend websocket disabled."
                : "BACKEND_URL is not configured; backend websocket disabled.");
            closeAllFrontendClients();
            return;
        }
        const socket = new WebSocket(url, {
            maxPayload: MAX_BACKEND_WEBSOCKET_PAYLOAD_BYTES,
        });
        socket.onopen = () => {
            console.info("WebSocket connected");
            if (reconnectTimeout) {
                clearTimeout(reconnectTimeout);
                reconnectTimeout = null;
            }
            socket.send(Buffer.from(backendApiKey, "utf-8"), { binary: false });
        };
        socket.onmessage = (event: WebSocket.MessageEvent) => {
            try {
                if (typeof event.data !== "string") {
                    socket.close(1003, "Backend websocket message must be text.");
                    closeAllFrontendClients();
                    return;
                }
                if (Buffer.byteLength(event.data, "utf-8") > MAX_BACKEND_WEBSOCKET_PAYLOAD_BYTES) {
                    socket.close(1003, "Backend websocket message exceeds maximum size.");
                    closeAllFrontendClients();
                    return;
                }
                const normalized = normalizeBackendMessage(event.data);
                if (!normalized) {
                    socket.close(1003, "Invalid backend websocket message.");
                    closeAllFrontendClients();
                    return;
                }

                const { topic, message } = normalized;
                if (!WEBSOCKET_TOPICS.has(topic)) return;
                lastMessage.set(topic, message);
                subscriptions.get(topic)?.forEach((client) => {
                    if (client.readyState === WebSocket.OPEN) client.send(message);
                });
            } catch {
                socket.close(1003, "Invalid backend websocket message.");
                closeAllFrontendClients();
            }
        };
        socket.onerror = (event: WebSocket.ErrorEvent) => console.error("WebSocket error:", event.message);
        socket.onclose = (event: WebSocket.CloseEvent) => {
            console.info(`WebSocket closed (code: ${event.code}, reason: ${event.reason})`);
            scheduleReconnect();
        };
    }
    function scheduleReconnect() {
        if (reconnectTimeout) clearTimeout(reconnectTimeout);
        reconnectTimeout = setTimeout(() => { reconnectTimeout = null; connect(); }, reconnectRetryDelay);
    }
    connect();
}

function getBackendWebsocketUrl(): string | undefined {
    const host = process.env.BACKEND_URL;
    return host ? `${host.replace(/\/$/, "")}/ws`.replace(/^http/, "ws") : undefined;
}

export const websocketServer = { initialize: (wss: WebSocketServer) => {
    // Origin is checked by ws before emitting `connection`, so a cookie cannot
    // turn a missing, null, malformed, or cross-origin request into a session.
    wss.options.verifyClient = ({ origin, req }: { origin: string; req: IncomingMessage }) =>
        isAllowedWebsocketOrigin(req, origin, process.env);
    wss.options.maxPayload = WEBSOCKET_MAX_PAYLOAD;
    initializeWebsocketServer(wss);
} };

import type { Route } from "./+types/route";
import { backendClient, type AdminSettingsRequest } from "~/clients/backend-client.server";
import { redirect } from "react-router";
import { getCsrfToken, validateCsrfToken } from "~/onboarding/onboarding-csrf.server";
import { isAuthenticated } from "~/auth/authentication.server";
import { DEFAULT_NO_CACHE_HEADERS, readBoundedRequestBody } from "~/onboarding/onboarding-request.server";

const MAX_BODY_BYTES = 64 * 1024;

const FORBIDDEN_ADMIN_KEYS = new Set(["api.strm-key"]);

const sanitizeAdminSettingsPayload = (payload: unknown): AdminSettingsRequest | null => {
    if (!payload || typeof payload !== "object" || Array.isArray(payload))
        return null;

    const request = payload as AdminSettingsRequest;
    if (!request.config || typeof request.config !== "object" || Array.isArray(request.config))
        return null;

    for (const key of Object.keys(request.config))
        if (FORBIDDEN_ADMIN_KEYS.has(key.toLowerCase()))
            return null;

    for (const key of request.clearSecrets ?? [])
        if (typeof key !== "string" || FORBIDDEN_ADMIN_KEYS.has(key.toLowerCase()))
            return null;

    return request;
};

export const __sanitizeAdminSettingsPayload = sanitizeAdminSettingsPayload;

const mergeNoCache = (csrfHeaders: HeadersInit = {}): Headers => {
    const headers = new Headers(DEFAULT_NO_CACHE_HEADERS);
    for (const [key, value] of new Headers(csrfHeaders).entries()) {
        headers.set(key, value);
    }
    return headers;
};

const resolveCsrfHeaders = async (request: Request, csrfToken: string | null): Promise<{ headers: Headers; tokenAccepted: boolean; }> => {
    if (csrfToken) {
        try {
            const refreshed = await validateCsrfToken(request, new URLSearchParams({ csrfToken }));
            return { headers: new Headers(refreshed.headers || {}), tokenAccepted: true };
        } catch {
            const refreshed = await getCsrfToken(request);
            const headers = new Headers(refreshed.headers || {});
            headers.set("X-CSRF-Token", refreshed.token);
            return { headers, tokenAccepted: false };
        }
    }

    const refreshed = await getCsrfToken(request);
    const headers = new Headers(refreshed.headers || {});
    headers.set("X-CSRF-Token", refreshed.token);
    return { headers, tokenAccepted: false };
};

export async function action({ request }: Route.ActionArgs) {
    if (!await isAuthenticated(request)) return redirect("/login");

    const csrfToken = request.headers.get("x-csrf-token");
    let csrfHeaders: Headers;
    let tokenAccepted: boolean;
    try {
        const result = await resolveCsrfHeaders(request, csrfToken);
        csrfHeaders = result.headers;
        tokenAccepted = result.tokenAccepted;
    } catch {
        return Response.json({ error: "CSRF validation failed." }, { status: 403, headers: mergeNoCache() });
    }

    if (request.method.toUpperCase() !== "POST" ||
        request.headers.get("content-type")?.split(";", 1)[0].trim().toLowerCase() !== "application/json") {
        return Response.json({ error: "Invalid request payload." }, { status: 400, headers: mergeNoCache(csrfHeaders) });
    }

    // A stale token is never permission to perform the action. The refreshed
    // token is returned with the canonical 403 so the browser can explicitly
    // replay this known-idempotent settings save once.
    if (!csrfToken || !tokenAccepted) {
        return Response.json({ error: "CSRF validation failed." }, { status: 403, headers: mergeNoCache(csrfHeaders) });
    }

    try {
        const raw = await readBoundedRequestBody(request, MAX_BODY_BYTES);
        const payload = JSON.parse(raw);
        const adminSettingsRequest = sanitizeAdminSettingsPayload(payload);
        if (!adminSettingsRequest || !adminSettingsRequest.config || Array.isArray(adminSettingsRequest.config) ||
            Object.keys(adminSettingsRequest.config).length > 128 ||
            (adminSettingsRequest.clearSecrets !== undefined && (!Array.isArray(adminSettingsRequest.clearSecrets) || adminSettingsRequest.clearSecrets.length > 16))) {
            return Response.json({ error: "Invalid request payload." }, { status: 400, headers: mergeNoCache(csrfHeaders) });
        }
        const result = await backendClient.updateAdminSettings(adminSettingsRequest);
        return Response.json(result, {
            headers: mergeNoCache(csrfHeaders),
        });
    } catch {
        return Response.json({ error: "Unable to update settings." }, { status: 400, headers: mergeNoCache(csrfHeaders) });
    }
}

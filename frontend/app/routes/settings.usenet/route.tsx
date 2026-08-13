import type { Route } from "./+types/route";
import { backendClient, type UsenetSettingsRequest } from "~/clients/backend-client.server";
import { redirect } from "react-router";
import { isAuthenticated } from "~/auth/authentication.server";
import { getCsrfToken, validateCsrfToken } from "~/onboarding/onboarding-csrf.server";
import { DEFAULT_NO_CACHE_HEADERS, readBoundedRequestBody } from "~/onboarding/onboarding-request.server";

const MAX_BODY_BYTES = 64 * 1024;

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

async function readBody(request: Request): Promise<string> {
    try {
        return await readBoundedRequestBody(request, MAX_BODY_BYTES);
    } catch {
        throw new Error("bad");
    }
}

export async function action({ request }: Route.ActionArgs) {
    if (!await isAuthenticated(request)) return redirect("/login");
    const csrfToken = request.headers.get("x-csrf-token");
    let csrfHeaders: Headers;
    let tokenAccepted: boolean;

    try {
        const resolved = await resolveCsrfHeaders(request, csrfToken);
        csrfHeaders = resolved.headers;
        tokenAccepted = resolved.tokenAccepted;
    } catch {
        return Response.json({ error: "CSRF validation failed." }, { status: 403, headers: mergeNoCache() });
    }

    if (request.method.toUpperCase() !== "POST" ||
        request.headers.get("content-type")?.split(";", 1)[0].trim().toLowerCase() !== "application/json") {
        return Response.json({ error: "Invalid request payload." }, { status: 400, headers: mergeNoCache(csrfHeaders) });
    }

    const saveUsenetSettings = async (tokenHeaders: Headers) => {
        try {
            const payload = JSON.parse(await readBody(request)) as UsenetSettingsRequest;
            const result = await backendClient.updateUsenetSettings(payload);
            return Response.json(result, { headers: mergeNoCache(tokenHeaders) });
        } catch (error) {
            const status = error instanceof Error && "status" in error && (error as { status?: number }).status === 409 ? 409 : 400;
            return Response.json({ error: status === 409 ? "Settings changed in another tab." : "Invalid provider settings." }, { status, headers: mergeNoCache(tokenHeaders) });
        }
    };

    // Never mutate on a stale token. Return the canonical fresh token with a
    // 403; only the explicitly idempotent browser settings client may replay.
    if (!csrfToken || !tokenAccepted) {
        return Response.json({ error: "CSRF validation failed." }, { status: 403, headers: mergeNoCache(csrfHeaders) });
    }

    return saveUsenetSettings(csrfHeaders);
}

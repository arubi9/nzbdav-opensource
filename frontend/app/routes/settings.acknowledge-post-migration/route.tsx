import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { redirect } from "react-router";
import { isAuthenticated } from "~/auth/authentication.server";
import { validateCsrfToken } from "~/onboarding/onboarding-csrf.server";
import { DEFAULT_NO_CACHE_HEADERS, readBoundedRequestBody } from "~/onboarding/onboarding-request.server";

function noCacheCsrfHeaders(csrfHeaders: HeadersInit): Headers {
    const headers = new Headers(csrfHeaders);
    for (const [key, value] of new Headers(DEFAULT_NO_CACHE_HEADERS).entries()) headers.set(key, value);
    return headers;
}

export async function action({ request }: Route.ActionArgs) {
    if (!await isAuthenticated(request)) return redirect("/login");
    if (request.method.toUpperCase() !== "POST") {
        return Response.json({ error: "Invalid request payload." }, { status: 400, headers: DEFAULT_NO_CACHE_HEADERS });
    }

    const contentLength = request.headers.get("content-length");
    if (contentLength !== null && (!/^\d+$/.test(contentLength) || Number(contentLength) !== 0)) {
        return Response.json({ error: "Invalid request payload." }, { status: 400, headers: DEFAULT_NO_CACHE_HEADERS });
    }
    // Chunked requests are bounded and have the same application deadline as
    // settings forms; a stalled stream must not reach the mutation.
    try {
        await readBoundedRequestBody(request, 0);
    } catch {
        return Response.json({ error: "Invalid request payload." }, { status: 400, headers: DEFAULT_NO_CACHE_HEADERS });
    }

    const token = request.headers.get("x-csrf-token");
    if (!token) return Response.json({ error: "CSRF validation failed." }, { status: 403, headers: DEFAULT_NO_CACHE_HEADERS });
    let csrfHeaders: HeadersInit;
    try {
        csrfHeaders = (await validateCsrfToken(request, new URLSearchParams({ csrfToken: token }))).headers || {};
    } catch {
        return Response.json({ error: "CSRF validation failed." }, { status: 403, headers: DEFAULT_NO_CACHE_HEADERS });
    }

    try {
        await backendClient.acknowledgePostMigration();
        return Response.json({ ok: true }, { headers: noCacheCsrfHeaders(csrfHeaders) });
    } catch {
        return Response.json({ error: "Unable to acknowledge migration." }, { status: 500, headers: noCacheCsrfHeaders(csrfHeaders) });
    }
}

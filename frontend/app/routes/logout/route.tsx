import { getCsrfToken } from "~/onboarding/onboarding-csrf.server";
import type { Route } from "./+types/route";
import { logout } from "~/auth/authentication.server";
import { backendClient } from "~/clients/backend-client.server";
import { clearSetupSession, getSetupGrantFromSession } from "~/onboarding/setup-session.server";
import { DEFAULT_NO_CACHE_HEADERS, parseOnboardingMutationForm } from "~/onboarding/onboarding-request.server";
import { validateCsrfToken } from "~/onboarding/onboarding-csrf.server";
import { redirect } from "react-router";

type HeaderCarrier = { headers?: HeadersInit };

function mergeResponseHeaders(responseInits: HeaderCarrier[]): ResponseInit {
  const headers = new Headers({
    ...DEFAULT_NO_CACHE_HEADERS,
    "Cache-Control": "private, no-store, no-cache, must-revalidate, max-age=0",
  });

  for (const responseInit of responseInits) {
    if (!responseInit.headers) continue;
    const sourceHeaders = new Headers(responseInit.headers);
    const sourceSetCookies = typeof (sourceHeaders as Headers & { getSetCookie?: () => string[] }).getSetCookie === "function"
      ? (sourceHeaders as Headers & { getSetCookie: () => string[] }).getSetCookie()
      : [];
    for (const [name, value] of sourceHeaders.entries()) {
      if (name.toLowerCase() !== "set-cookie") headers.append(name, value);
    }
    if (sourceSetCookies.length > 0) {
      for (const cookie of sourceSetCookies) headers.append("Set-Cookie", cookie);
    } else {
      const cookie = sourceHeaders.get("set-cookie");
      if (cookie) headers.append("Set-Cookie", cookie);
    }
  }

  return { headers };
}

const createNoCacheResponse = (payload: unknown, responseInits: HeaderCarrier[] = [], status = 200): Response => {
  return Response.json(payload, { ...mergeResponseHeaders(responseInits.concat({ headers: DEFAULT_NO_CACHE_HEADERS })), status });
};

const mapLogoutError = (status?: number): boolean => {
  if (status === 401 || status === 410) return false;
  if (status === 403 || status === 404 || status === 503 || status === 500) return true;
  return true;
};

export async function action({ request }: Route.ActionArgs) {
  let parsedForm: URLSearchParams;
  try {
    parsedForm = await parseOnboardingMutationForm(request);
  } catch {
    const csrfRefresh = await getCsrfToken(request);
    return createNoCacheResponse(
      {
        error: "Invalid request payload.",
      },
      [csrfRefresh],
      400,
    );
  }

  let csrfHeaderInit: HeaderCarrier;
  try {
    csrfHeaderInit = await validateCsrfToken(request, parsedForm);
  } catch {
    const csrfRefresh = await getCsrfToken(request);
    return createNoCacheResponse(
      {
        error: "Invalid CSRF token.",
      },
      [csrfRefresh],
      400,
    );
  }

  const setupGrant = await getSetupGrantFromSession(request);
  let revokeWarning = false;

  if (setupGrant.grant) {
    try {
      await backendClient.revokeSetupGrant(setupGrant.grant, { signal: request.signal });
    } catch (error) {
      const status = (error as { status?: number })?.status;
      const shouldWarn = mapLogoutError(status);
      revokeWarning = shouldWarn;
    }
  }

  const responseInit = await logout(request);
  const setupResponseInit = await clearSetupSession(request);

  const searchParams = revokeWarning ? "?warn=1" : "";
  const headers = mergeResponseHeaders([csrfHeaderInit, responseInit, setupResponseInit, { headers: DEFAULT_NO_CACHE_HEADERS }]);

  return redirect(`/login${searchParams}`, headers);
}

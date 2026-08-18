import { Alert, Button, Form as BootstrapForm } from "react-bootstrap";
import { Form, redirect, useNavigation } from "react-router";
import styles from "./route.module.css";
import type { Route } from "./+types/route";
import { isAuthenticated, loginWithCredentials } from "~/auth/authentication.server";
import { getCsrfToken, validateCsrfToken } from "~/onboarding/onboarding-csrf.server";
import { backendClient } from "~/clients/backend-client.server";
import { DEFAULT_NO_CACHE_HEADERS, parseOnboardingMutationForm } from "~/onboarding/onboarding-request.server";

type HeaderCarrier = { headers?: HeadersInit };

function mergeResponseHeaders(responseInits: HeaderCarrier[]): ResponseInit {
  const headers = new Headers();

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

const PRIVATE_NO_CACHE_HEADERS: HeadersInit = {
  ...DEFAULT_NO_CACHE_HEADERS,
  "Cache-Control": `private, ${(DEFAULT_NO_CACHE_HEADERS as Record<string, string>)["Cache-Control"]}`,
};

const loginHeaders = (...responseInits: HeaderCarrier[]): ResponseInit => {
  return mergeResponseHeaders(responseInits.concat({ headers: PRIVATE_NO_CACHE_HEADERS }));
};

type LoginPageData = {
  loginError: string | null;
  csrfToken?: string;
  returnTo?: string;
};

const ALLOWED_ONBOARDING_STEPS = new Set(["services", "jellyfin", "usenet", "indexers", "configure", "ready"]);

/** Only local onboarding routes are accepted; absolute and protocol-relative
 * URLs are never carried through the login flow. */
export function getSafeReturnToValue(raw: string | null | undefined): string {
  if (!raw || raw.startsWith("//") || !raw.startsWith("/"))
    return "/";

  try {
    const parsed = new URL(raw, "http://local.invalid");
    if (parsed.origin !== "http://local.invalid" || parsed.pathname !== "/onboarding")
      return "/";
    const step = parsed.searchParams.get("step");
    if (step && !ALLOWED_ONBOARDING_STEPS.has(step))
      return "/";
    return `/onboarding${step ? `?step=${encodeURIComponent(step)}` : ""}`;
  } catch {
    return "/";
  }
}

export function getSafeReturnTo(request: Request): string {
  return getSafeReturnToValue(new URL(request.url).searchParams.get("returnTo"));
}

export const getSafeLoginError = (error: unknown): string => {
  // Authentication failures are intentionally a closed set.  In particular,
  // do not forward backend URLs, status text, or Error.message to the page.
  const code = error && typeof error === "object" && "code" in error
    ? String((error as { code?: unknown }).code).toLowerCase()
    : "";
  if (code === "invalid-credentials" || code === "unauthorized") {
    return "Invalid credentials.";
  }
  if (code === "missing-credentials") {
    return "Username and password are required.";
  }
  if (code === "timeout") {
    return "Sign-in timed out. Please try again.";
  }
  if (code === "backend-unavailable") return "Backend service is unavailable.";
  return "Unable to sign in.";
};

export async function loader({ request }: Route.LoaderArgs): Promise<LoginPageData | Response> {
  const returnTo = getSafeReturnTo(request);
  // if already logged in, redirect to the validated local destination
  try {
    if (await isAuthenticated(request)) {
      return redirect(returnTo, loginHeaders());
    }
  } catch {
    return Response.json(
      { loginError: "Unable to sign in.", csrfToken: undefined },
      loginHeaders(),
    );
  }

  // if onboarding flow is enabled, redirect to onboarding page
  try {
    const isOnboarding = await backendClient.isOnboarding();
    if (isOnboarding) {
      return redirect(returnTo === "/" ? "/onboarding" : returnTo, loginHeaders());
    }
  } catch {
    return Response.json(
      { loginError: "Backend service is unavailable.", csrfToken: undefined },
      loginHeaders(),
    );
  }

  const csrf = await getCsrfToken(request);
  return Response.json(
    {
      loginError: null,
      csrfToken: csrf.token,
      returnTo,
    } as LoginPageData,
    loginHeaders(csrf),
  );
}

export async function action({ request }: Route.ActionArgs): Promise<LoginPageData | Response> {
  let returnTo = getSafeReturnTo(request);
  let formData: URLSearchParams;
  try {
    formData = await parseOnboardingMutationForm(request);
    if (returnTo === "/")
      returnTo = getSafeReturnToValue(formData.get("returnTo"));
  } catch {
    const csrf = await getCsrfToken(request);
    return Response.json(
      {
        loginError: "Invalid request payload.",
        csrfToken: csrf.token,
      },
      loginHeaders(csrf),
    );
  }

  let csrfHeaders: HeaderCarrier;
  try {
    const csrfValidate = await validateCsrfToken(request, formData);
    csrfHeaders = { headers: csrfValidate.headers };
  } catch {
    const csrf = await getCsrfToken(request);
    return Response.json(
      {
        loginError: "CSRF token is missing or invalid.",
        csrfToken: csrf.token,
      },
      loginHeaders(csrf),
    );
  }

  const username = formData.get("username")?.trim() || "";
  const password = formData.get("password")?.toString() || "";

  if (!username || !password) {
    const csrf = await getCsrfToken(request);
    return Response.json(
      {
        loginError: "Username and password are required.",
        csrfToken: csrf.token,
      },
      loginHeaders(csrfHeaders, csrf),
    );
  }

  try {
    const loginResponse = await loginWithCredentials(request, username, password);
    return redirect(returnTo, loginHeaders(csrfHeaders, loginResponse));
  } catch (error) {
    const csrf = await getCsrfToken(request);
    return Response.json(
      {
        loginError: getSafeLoginError(error),
        csrfToken: csrf.token,
      },
      loginHeaders(csrfHeaders, csrf),
    );
  }
}

export default function Index({ loaderData, actionData }: Route.ComponentProps) {
  const navigation = useNavigation();
  const isLoading = navigation.state == "submitting";
  const pageData = (actionData || loaderData) as LoginPageData;
  const showError = !!pageData?.loginError;
  const submitButtonDisabled = isLoading;
  const submitButtonText = isLoading ? "Logging in..." : "Login";

  return (
    <Form className={styles["container"]} method="POST">
      <img className={styles["logo"]} src="/logo.svg"></img>
      <div className={styles["title"]}>Nzb DAV</div>
      <Alert className={styles["error"]} show={showError} variant="danger">
        {pageData?.loginError}
      </Alert>
      <input type="hidden" name="csrfToken" value={pageData?.csrfToken || ""} />
      {pageData?.returnTo && pageData.returnTo !== "/" ? <input type="hidden" name="returnTo" value={pageData.returnTo} /> : null}
      <BootstrapForm.Control name="username" type="text" placeholder="Username" autoComplete="username" autoFocus />
      <BootstrapForm.Control name="password" type="password" placeholder="Password" autoComplete="current-password" />
      <Button type="submit" variant="primary" disabled={submitButtonDisabled}>{submitButtonText}</Button>
    </Form>
  );
}

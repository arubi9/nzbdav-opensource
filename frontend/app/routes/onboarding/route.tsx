import { useEffect, useMemo, useState } from "react";
import { Alert, Button, Form as BootstrapForm } from "react-bootstrap";
import { Form, redirect, useNavigation } from "react-router";
import styles from "./route.module.css";
import type { Route } from "./+types/route";
import { isAuthenticated, setSessionUser } from "~/auth/authentication.server";
import {
  backendClient,
  type SetupIndexerPayload,
  type SetupStatus,
  type SetupServiceStatus,
  type SetupUsenetProviderPayload,
  type SetupStepStatus,
  type SetupReasonCode,
} from "~/clients/backend-client.server";
import {
  addSetupIndexer,
  addSetupProvider,
  clearSetupSession,
  createSetupSession,
  getSetupDraftFromSession,
  getSetupSessionState,
  removeSetupIndexer,
  removeSetupProvider,
  type SetupDraftIndexerSummary,
  type SetupDraftSummaryItem,
  type SetupSessionState,
} from "~/onboarding/setup-session.server";
import {
  DEFAULT_NO_CACHE_HEADERS,
  parseOnboardingMutationForm,
} from "~/onboarding/onboarding-request.server";
import {
  getJellyfinSetupLinkHint,
  resolveBrowserJellyfinSetupUrl,
  type JellyfinSetupLinkHint,
  FALLBACK_JELLYFIN_URL,
} from "~/onboarding/jellyfin-link";
import { getCsrfToken, validateCsrfToken } from "~/onboarding/onboarding-csrf.server";
import { updateFromInput } from "./input-change";
const WIZARD_STEPS = ["services", "jellyfin", "usenet", "indexers", "configure", "repair", "ready"] as const;

type WizardStep = (typeof WIZARD_STEPS)[number];

const REQUIRED_LIVE_SERVICES = ["nzbdav", "jellyfin", "sonarr", "radarr", "prowlarr"] as const;

const liveServicesReady = (status: SetupStatus): boolean =>
  REQUIRED_LIVE_SERVICES.every((name) => status.services.some((service) => service.name === name && service.ready));

const stepOrder: Record<WizardStep, number> = {
  services: 0,
  jellyfin: 1,
  usenet: 2,
  indexers: 3,
  configure: 4,
  repair: 5,
  ready: 6,
};

const MAX_PROVIDER_FIELDS = 32;
const MAX_INDEXER_FIELDS = 100;

type ProviderDraftInputs = {
  Host: string;
  Port: string;
  UseSsl: boolean;
  User: string;
  MaxConnections: string;
  Type: number;
};

type IndexerDraftInputs = {
  Name: string;
  Url: string;
  AllowPrivateNetwork: boolean;
};

type SetupWizardLoaderData =
  | {
      mode: "legacy";
      error: string | null;
      csrfToken: string;
    }
  | {
      mode: "fullstack";
      error: string | null;
      activeStep: WizardStep;
      setupSessionState: SetupSessionState;
      setupStatus: SetupStatus;
      hasRecoveredSession: boolean;
      csrfToken: string;
      jellyfinSetupHint: JellyfinSetupLinkHint;
    };


type SetupWizardActionData =
  | {
      mode: "legacy";
      error: string;
      step?: never;
    }
  | {
      mode: "fullstack";
      step: WizardStep;
      error: string;
      setupStatus?: SetupStatus;
      retryAfterSeconds?: number;
    };

export function sanitizeWizardStep(
  rawStep: string | null,
  setupSessionState: SetupSessionState,
  status: SetupStatus,
): WizardStep {
  // Completion is not sufficient to enter Ready: a completed installation
  // with stale managed resources has its own re-authenticated repair boundary.
  // Check this before the broad completed case so a stale ?step=ready can
  // never bypass repair.
  if (status.completed && status.repairRequired) {
    return "repair";
  }

  if (status.completed) {
    // The completion marker is not a live-service assertion. During a
    // contended verification all services are explicitly busy/not-ready;
    // keep the wizard on the status view instead of rendering Ready.
    return liveServicesReady(status) ? "ready" : "services";
  }

  if (!setupSessionState.hasSetupSession) {
    if (rawStep === "jellyfin") {
      return "jellyfin";
    }
    return "services";
  }

  let maxAllowed: WizardStep = "services";
  if (!setupSessionState.hasProviderDraft) {
    maxAllowed = "usenet";
  } else if (!setupSessionState.hasIndexerDraft) {
    maxAllowed = "indexers";
  } else {
    maxAllowed = "configure";
  }

  if (rawStep && stepOrder[rawStep as WizardStep] !== undefined) {
    const requested = rawStep as WizardStep;
    if (stepOrder[requested] <= stepOrder[maxAllowed]) {
      return requested;
    }

    return maxAllowed;
  }

  return maxAllowed;
}

const DEFAULT_PROVIDER_DRAFT: ProviderDraftInputs = {
  Host: "",
  Port: "563",
  UseSsl: true,
  User: "",
  MaxConnections: "4",
  Type: 1,
};

const DEFAULT_INDEXER_DRAFT: IndexerDraftInputs = {
  Name: "",
  Url: "",
  AllowPrivateNetwork: false,
};

type HeaderInitCarrier = { headers?: HeadersInit };

const asHeaderCarrier = (headers?: HeadersInit): HeaderInitCarrier => ({ headers: headers || {} });

const mergeResponseHeaders = (responseInits: HeaderInitCarrier[]): ResponseInit => {
  const headers = new Headers({
    ...DEFAULT_NO_CACHE_HEADERS,
    "Cache-Control": "private, no-store, no-cache, must-revalidate, max-age=0",
  });

  for (const responseInit of responseInits) {
    if (!responseInit.headers) continue;
    const source = new Headers(responseInit.headers);
    for (const [name, value] of source.entries()) {
      headers.append(name, value);
    }
  }

  return { headers };
};

const createNoCacheResponse = (payload: unknown, responseInits: HeaderInitCarrier[] = [], status = 200): Response => {
  return Response.json(payload, { ...mergeResponseHeaders(responseInits), status });
};

// The Express frontend proxy authenticates the request before the backend
// trusts this bounded source. It is optional for direct/test requests.
const frontendClientSourceFor = (request: Request): string | undefined => {
  // Express injects this opaque value after its trusted-proxy boundary. Never
  // derive a throttle bucket from browser-supplied forwarding headers.
  const value = (request.headers.get("x-frontend-client-source") || "").trim();
  return /^[A-Za-z0-9_-]{43}$/.test(value) ? value : undefined;
};

const toErrorStep = (status: SetupStatus): WizardStep => {
  if (status.completed) {
    if (status.repairRequired) return "repair";
    return liveServicesReady(status) ? "ready" : "services";
  }

  if (!status.steps || status.steps.length === 0) {
    return "configure";
  }

  const failedStep = status.steps.find((step) => step.state === "failed" || step.state === "warning");
  if (failedStep) {
    return "configure";
  }

  return "configure";
};

const safeRetryAfterSeconds = (value: unknown): number | undefined => {
  if (typeof value !== "string" || !/^[1-9]\d{0,1}$/.test(value)) return undefined;
  const seconds = Number(value);
  return seconds <= 30 ? seconds : undefined;
};

export const getErrorFromMutationError = (error: unknown): string => {
  const code = error && typeof error === "object" && "code" in error
    ? String((error as { code?: unknown }).code).toLowerCase()
    : "";
  switch (code) {
    case "timeout":
      return "Setup request timed out.";
    case "access-denied":
      return "Access denied.";
    case "rate-limited":
      return "Too many setup attempts. Please retry later.";
    case "conflict":
      return "Setup conflicts with the current state.";
    case "backend-unavailable":
      return "Backend service is unavailable.";
    case "bad-request":
      return "The setup request was invalid.";
    case "not-found":
      return "The setup resource was not found.";
    default:
      return "Setup operation failed.";
  }
};

const getRuntimeSetupStatus = async (): Promise<SetupStatus | null> => {
  try {
    return await backendClient.getSetupStatus();
  } catch (error) {
    const status = (error as { status?: number } | undefined)?.status;
    if (status === 404) {
      return null;
    }

    throw error;
  }
};

type SetupOnboardingMode =
  | {
      mode: "legacy";
    }
  | {
      mode: "fullstack";
      setupStatus: SetupStatus;
    };

async function getOnboardingMode(): Promise<SetupOnboardingMode> {
  const status = await getRuntimeSetupStatus();
  if (status?.enabled) {
    return { mode: "fullstack", setupStatus: status };
  }

  if (await backendClient.isOnboarding()) {
    return { mode: "legacy" };
  }

  return { mode: "legacy" };
}

function providerErrorMessage(form: Record<string, string>): string | null {
  if (!form.Host.trim() || form.Host.length > 255) return "Provider host is required (max 255 chars).";
  if (!/^[\w.-]+$/.test(form.Host)) return "Invalid provider host.";
  if (!form.Port || Number.isNaN(Number(form.Port))) return "Port must be numeric.";

  const port = Number(form.Port);
  if (!Number.isInteger(port) || port < 1 || port > 65535) return "Port must be between 1 and 65535.";

  if (!form.User.trim() || form.User.length > 128) return "Username is required (max 128 chars).";
  if (!form.MaxConnections || Number.isNaN(Number(form.MaxConnections))) return "Max connections must be numeric.";
  const maxConnections = Number(form.MaxConnections);
  if (!Number.isInteger(maxConnections) || maxConnections < 1 || maxConnections > 10_000) return "Max connections must be between 1 and 10000.";

  const pass = form.Pass;
  if (!pass || pass.length > 256) return "Password is required (max 256 chars).";

  return null;
}

function indexerErrorMessage(form: Record<string, string>): string | null {
  if (!form.Name.trim() || form.Name.length > 128) return "Indexer name is required (max 128 chars).";

  const url = form.Url.trim();
  if (!url) return "Indexer URL is required.";
  if (url.length > 2048) return "Indexer URL is too long.";

  try {
    const parsed = new URL(url);
    if (!/^https?:$/i.test(parsed.protocol) || parsed.username || parsed.password || parsed.hash) {
      return "Indexer URL must be http/https without credentials or fragment.";
    }
  } catch {
    return "Indexer URL must be a valid URL.";
  }

  if (!form.ApiKey || form.ApiKey.length > 512) return "Indexer API key is required (max 512 chars).";

  return null;
}

const mapFailureMessage = (status: SetupStatus): string | null => {
  const failed = status.steps.find((step) => step.state === "failed" || step.state === "warning");
  if (!failed) return null;

  const safeName = failed.name;
  const reasonMessages: Record<SetupReasonCode, string> = {
    "nzbdav-failed": "NZBDAV API/plugin readiness verification failed.",
    "sonarr-failed": "Sonarr managed resources are not ready.",
    "radarr-failed": "Radarr managed resources are not ready.",
    "jellyfin-failed": "Jellyfin live readiness verification failed.",
    "provider-failed": "The provider could not be verified.",
    "provider-auth-failed": "The provider credentials were not accepted.",
    "indexer-failed": "The indexer could not be verified.",
    "indexer-capability-failed": "The indexer capability check failed.",
    "configuration-failed": "The setup configuration was rejected.",
    "compatibility-failed": "A required service or plugin is incompatible.",
    "jellyfin-version-failed": "The Jellyfin version is not supported.",
    "library-collision": "Jellyfin library paths conflict; review the library paths and retry.",
    "library-failed": "The Jellyfin libraries are not ready.",
    "task-failed": "The Jellyfin sync task is not ready.",
    "plugin-mismatch": "The NZBDAV Jellyfin plugin configuration was not retained.",
    "arr-failed": "Sonarr or Radarr health verification failed.",
    "prowlarr-failed": "Prowlarr health verification failed.",
    "validation-canceled": "Validation was cancelled; retry setup.",
    "backend-unavailable": "Backend service is unavailable.",
    "setup-run-busy": "A setup verification is busy; retry shortly.",
    unknown: "Please adjust configuration and retry.",
  };
  const reason = failed.reason && Object.hasOwn(reasonMessages, failed.reason)
    ? failed.reason
    : "unknown";
  return `${safeName}: ${reasonMessages[reason]}`;
};

const legacySafeMessage = (status?: number): string | null => {
  switch (status) {
    case 400:
      return "Registration request was invalid.";
    case 401:
    case 403:
      return "Credentials were not accepted.";
    case 409:
      return "An account conflict occurred.";
    case 503:
      return "Backend service is unavailable.";
    default:
      return null;
  }
};

/**
 * React Router's document default forwards only Set-Cookie from child
 * loaders/actions. The public handoff capability and no-store policy must be
 * forwarded explicitly; action values win because validation rotates the
 * token before the browser follows the response.
 */
export const headers: Route.HeadersFunction = ({ loaderHeaders, actionHeaders, errorHeaders }) => {
  const merged = new Headers(errorHeaders || loaderHeaders);
  const sourceSetCookies = typeof (actionHeaders as Headers & { getSetCookie?: () => string[] }).getSetCookie === "function"
    ? (actionHeaders as Headers & { getSetCookie: () => string[] }).getSetCookie()
    : [];
  for (const [name, value] of actionHeaders.entries()) {
    if (name.toLowerCase() !== "set-cookie") merged.set(name, value);
  }
  if (sourceSetCookies.length > 0) {
    for (const cookie of sourceSetCookies) merged.append("Set-Cookie", cookie);
  } else {
    const cookie = actionHeaders.get("set-cookie");
    if (cookie) merged.append("Set-Cookie", cookie);
  }
  return merged;
};

export async function loader({ request }: Route.LoaderArgs) {
  let isAuth = false;
  try {
    isAuth = await isAuthenticated(request);
  } catch {
    isAuth = false;
  }
  let csrf: Awaited<ReturnType<typeof getCsrfToken>>;
  try {
    csrf = await getCsrfToken(request);
  } catch (error) {
    const status = (error as { status?: number } | undefined)?.status;
    return createNoCacheResponse(
      { mode: "legacy", error: "CSRF service temporarily unavailable.", csrfToken: "" },
      [{ headers: DEFAULT_NO_CACHE_HEADERS }],
      status === 429 || status === 503 ? status : 503,
    );
  }

  // This is a public, same-origin bootstrap capability rather than a
  // credential.  The browser needs it before its first unauthenticated
  // handoff; the HttpOnly cookie binds it to this browser session. It is
  // intentionally no-store and is never made CORS-readable.
  const csrfResponseHeaders = new Headers(csrf.headers);
  csrfResponseHeaders.set("X-CSRF-Token", csrf.token);

  let setupMode: SetupOnboardingMode;
  try {
    setupMode = await getOnboardingMode();
  } catch {
    const responseHeaders: HeaderInitCarrier[] = [asHeaderCarrier(csrfResponseHeaders), { headers: DEFAULT_NO_CACHE_HEADERS }];
    return createNoCacheResponse(
      { mode: "legacy", error: "Backend service is unavailable.", csrfToken: csrf.token },
      responseHeaders,
    );
  }
  const csrfHeadersForLoader = asHeaderCarrier(csrfResponseHeaders);
  const responseHeaders: HeaderInitCarrier[] = [csrfHeadersForLoader, { headers: DEFAULT_NO_CACHE_HEADERS }];

  if (setupMode.mode === "legacy") {
    let isOnboarding: boolean;
    try {
      isOnboarding = await backendClient.isOnboarding();
    } catch {
      return createNoCacheResponse(
        { mode: "legacy", error: "Backend service is unavailable.", csrfToken: csrf.token },
        responseHeaders,
      );
    }
    if (!isOnboarding) {
      if (!isAuth) {
        return redirect("/login?returnTo=%2Fonboarding", mergeResponseHeaders(responseHeaders));
      }

      return redirect("/", mergeResponseHeaders(responseHeaders));
    }

    if (isAuth) {
      return redirect("/", mergeResponseHeaders(responseHeaders));
    }

    return createNoCacheResponse(
      {
        mode: "legacy",
        error: null,
        csrfToken: csrf.token,
      },
      responseHeaders,
    );
  }

  const setupStatus = setupMode.setupStatus;
  const setupSessionState = await getSetupSessionState(request);

  // Handoff is intentionally unauthenticated, but the wizard after handoff is not.
  // Do this before loading any draft so an auth-less request cannot reach setup secrets.
  if ((setupSessionState.hasSetupSession || setupSessionState.hasRecoveredSession) && !isAuth) {
    return redirect("/login?returnTo=%2Fonboarding", mergeResponseHeaders(responseHeaders));
  }

  const requestedStep = new URL(request.url).searchParams.get("step");
  const activeStep = sanitizeWizardStep(requestedStep, setupSessionState, setupStatus);

  return createNoCacheResponse(
    {
      mode: "fullstack",
      error: null,
      activeStep,
      setupSessionState,
      setupStatus,
      hasRecoveredSession: setupSessionState.hasRecoveredSession,
      csrfToken: csrf.token,
      jellyfinSetupHint: getJellyfinSetupLinkHint(),
    },
    responseHeaders,
  );
}

export async function action({ request }: Route.ActionArgs): Promise<SetupWizardActionData | Response> {
  const setupMode = await getOnboardingMode();
  const isFullstack = setupMode.mode === "fullstack";
  const setupStatus = setupMode.mode === "fullstack" ? setupMode.setupStatus : null;

  let formData: URLSearchParams;
  try {
    formData = await parseOnboardingMutationForm(request);
  } catch {
    try {
      const csrfRefresh = await getCsrfToken(request);
      return createNoCacheResponse(
        {
          mode: isFullstack ? "fullstack" : "legacy",
          error: "Invalid request payload.",
        },
        [asHeaderCarrier(csrfRefresh.headers), { headers: DEFAULT_NO_CACHE_HEADERS }],
      );
    } catch (error) {
      const status = (error as { status?: number } | undefined)?.status;
      return createNoCacheResponse(
        { mode: isFullstack ? "fullstack" : "legacy", error: "CSRF service temporarily unavailable." },
        [{ headers: DEFAULT_NO_CACHE_HEADERS }],
        status === 429 || status === 503 ? status : 503,
      );
    }
  }

  const action = formData.get("action")?.toString() || "";

  // Handoff and legacy registration are the only unauthenticated mutations. Check this
  // before CSRF rotation so a missing auth cookie cannot consume a setup mutation token.
  if (isFullstack && action !== "handoff") {
    let authenticated = false;
    try {
      authenticated = await isAuthenticated(request);
    } catch {
      authenticated = false;
    }
    if (!authenticated) return redirect("/login?returnTo=%2Fonboarding", { headers: DEFAULT_NO_CACHE_HEADERS });
  }

  let csrfHeaders: HeaderInitCarrier;
  try {
    const csrfValidate = await validateCsrfToken(request, formData);
    csrfHeaders = asHeaderCarrier(csrfValidate.headers);
  } catch (error) {
    const status = (error as { status?: number } | undefined)?.status;
    if (status === 429 || status === 503) {
      return createNoCacheResponse(
        { mode: isFullstack ? "fullstack" : "legacy", error: "CSRF service temporarily unavailable." },
        [{ headers: DEFAULT_NO_CACHE_HEADERS }],
        status,
      );
    }

    try {
      const csrfRefresh = await getCsrfToken(request);
      return createNoCacheResponse(
        {
          mode: isFullstack ? "fullstack" : "legacy",
          error: "CSRF token is missing or invalid.",
        },
        [asHeaderCarrier(csrfRefresh.headers), { headers: DEFAULT_NO_CACHE_HEADERS }],
      );
    } catch (refreshError) {
      const status = (refreshError as { status?: number } | undefined)?.status;
      return createNoCacheResponse(
        { mode: isFullstack ? "fullstack" : "legacy", error: "CSRF service temporarily unavailable." },
        [{ headers: DEFAULT_NO_CACHE_HEADERS }],
        status === 429 || status === 503 ? status : 503,
      );
    }
  }

  if (!isFullstack) {
    if (action !== "legacy-register") {
      return createNoCacheResponse(
        { mode: "legacy", error: "Unsupported legacy request." },
        [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
      );
    }

    const username = formData.get("username")?.toString() || "";
    const password = formData.get("password")?.toString() || "";
    const confirm = formData.get("confirm")?.toString() || "";

    if (!username || !password) {
      return createNoCacheResponse(
        { mode: "legacy", error: "Username and password are required." },
        [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
      );
    }

    if (password !== confirm) {
      return createNoCacheResponse(
        { mode: "legacy", error: "Passwords must match." },
        [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
      );
    }

    try {
      const created = await backendClient.createAccount(username, password);
      if (!created) {
        return createNoCacheResponse(
          { mode: "legacy", error: "Registration failed." },
          [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
        );
      }

      const sessionInit = await setSessionUser(request, username);
      return redirect("/", mergeResponseHeaders([csrfHeaders, sessionInit]));
    } catch (error) {
      const statusCode = (error as { status?: number })?.status;
      return createNoCacheResponse(
        {
          mode: "legacy",
          error: legacySafeMessage(statusCode) || "Registration failed. Try again.",
        },
        [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
      );
    }
  }

  if (!setupStatus) {
    return createNoCacheResponse(
      { mode: "legacy", error: "Setup endpoint unavailable." },
      [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
    );
  }

  // A completed installation is a separate security boundary. Even when
  // repair is required, do not let a stale wizard submit provider/indexer
  // drafts or run the normal configure path. Repair has its own explicit
  // re-authentication and retry actions below.
  if (setupStatus.completed
    && setupStatus.repairRequired
    && action !== "recover-revocation"
    && action !== "repair-handoff"
    && action !== "repair"
    && action !== "verify-services") {
    return createNoCacheResponse(
      { mode: "fullstack", step: "repair", error: "Setup is complete. Re-authenticate to repair managed resources." },
      [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
    );
  }

  if (setupStatus.completed
    && !setupStatus.repairRequired
    && !liveServicesReady(setupStatus)
    && action !== "recover-revocation"
    && action !== "repair-handoff"
    && action !== "repair"
    && action !== "verify-services") {
    return createNoCacheResponse(
      { mode: "fullstack", step: "services", error: "Managed services are still being checked; retry when verification is no longer busy." },
      [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
    );
  }

  if (setupStatus.completed
    && liveServicesReady(setupStatus)
    && (!setupStatus.repairRequired || (action !== "repair-handoff" && action !== "repair"))
    && action !== "recover-revocation"
    && action !== "verify-services") {
    return createNoCacheResponse(
      {
        mode: "fullstack",
        step: setupStatus.repairRequired ? "repair" : "ready",
        error: setupStatus.repairRequired
          ? "Setup is complete. Re-authenticate to repair managed resources."
          : "Setup already completed. Open NZBDAV to continue.",
      },
      [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
    );
  }

  // The Jellyfin handoff is the only unauthenticated setup mutation. Once it has issued
  // a grant, every draft/configure/retry/remove operation has passed the auth check above.
  try {
    const sessionState = await getSetupSessionState(request);

    switch (action) {
      case "verify-services": {
        // Verification is a bounded health/status command, not a setup
        // mutation. The frontend session plus this action's CSRF boundary
        // authorizes the server-side transport; the backend adds its private
        // FRONTEND_BACKEND_API_KEY and no setup grant crosses this path.
        const verified = await backendClient.verifySetupStatus({ signal: request.signal });
        const step = verified.repairRequired
          ? "repair"
          : verified.completed && liveServicesReady(verified)
            ? "ready"
            : "services";
        const reason = mapFailureMessage(verified)
          || (verified.services.some((service) => service.reason === "setup-run-busy")
            ? "Service verification is busy; retry shortly."
            : verified.completed ? "Managed services are not ready; retry verification." : "Service verification did not complete.");
        return createNoCacheResponse(
          { mode: "fullstack", step, setupStatus: verified, error: step === "ready" ? "Service verification complete." : reason },
          [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
        );
      }

      case "repair-handoff": {
        const username = formData.get("username")?.toString() || "";
        const password = formData.get("password")?.toString() || "";
        if (!username || !password) {
          return createNoCacheResponse({ mode: "fullstack", step: "repair", error: "Re-authenticate with the Jellyfin administrator credentials." }, [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]);
        }
        const grant = await backendClient.issueRepairGrant(username, password, {
          signal: request.signal,
          frontendClientSource: frontendClientSourceFor(request),
        });
        const sessionInit = await createSetupSession(request, grant.grant, grant.expiresAtUtc);
        const userSessionInit = await setSessionUser(request, username);
        return redirect("/onboarding?step=repair", mergeResponseHeaders([sessionInit, userSessionInit, csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]));
      }

      case "repair": {
        const draft = await getSetupDraftFromSession(request);
        if (!draft.grant) {
          return createNoCacheResponse({ mode: "fullstack", step: "repair", error: "Re-authenticate before repairing managed resources." }, [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]);
        }
        const status = await backendClient.retrySetup(draft.grant, { signal: request.signal });
        if (status.completed && !status.repairRequired) {
          try { await backendClient.revokeSetupGrant(draft.grant, { signal: request.signal, frontendClientSource: frontendClientSourceFor(request) }); } catch { }
          const clearSetup = await clearSetupSession(request);
          return redirect("/onboarding?step=ready", mergeResponseHeaders([clearSetup, csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]));
        }
        return createNoCacheResponse({ mode: "fullstack", step: "repair", error: "Managed-resource repair is still required. Retry after correcting the reported service." }, [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]);
      }

      case "recover-revocation": {
        const username = formData.get("username")?.toString() || "";
        const password = formData.get("password")?.toString() || "";
        if (!username || !password) {
          return createNoCacheResponse(
            { mode: "fullstack", step: "services", error: "Username and password are required for cleanup recovery." },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        const recovered = await backendClient.recoverSetupGrant(username, password, {
          signal: request.signal,
          frontendClientSource: frontendClientSourceFor(request),
        });
        if (recovered.revocationPending) {
          return createNoCacheResponse(
            { mode: "fullstack", step: "services", error: "Cleanup is still pending. Retry recovery." },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        // Recovery retires the old external session but intentionally does not
        // issue a grant. Drop the old local handle before a fresh handoff.
        const clearRecoveredSetup = await clearSetupSession(request);
        return redirect("/onboarding?step=jellyfin", mergeResponseHeaders([
          clearRecoveredSetup,
          csrfHeaders,
          { headers: DEFAULT_NO_CACHE_HEADERS },
        ]));
      }

      case "handoff": {
        const username = formData.get("username")?.toString() || "";
        const password = formData.get("password")?.toString() || "";

        if (!username || !password) {
          return createNoCacheResponse(
            { mode: "fullstack", step: "jellyfin", error: "Username and password are required." },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        const shouldTryRenewFirst = sessionState.hasSetupSession || sessionState.hasRecoveredSession;
        const doIssue = async () => {
          return backendClient.issueSetupGrant(username, password, {
            signal: request.signal,
            frontendClientSource: frontendClientSourceFor(request),
          });
        };

        const doRenew = async () => {
          return backendClient.renewSetupGrant(username, password, {
            signal: request.signal,
            frontendClientSource: frontendClientSourceFor(request),
          });
        };

        let grantResponse;
        if (shouldTryRenewFirst) {
          try {
            grantResponse = await doRenew();
          } catch (error) {
            const status = (error as { status?: number } | undefined)?.status;
            if (status === 400 || status === 404 || status === 410) {
              grantResponse = await doIssue();
            } else {
              throw error;
            }
          }
        } else {
          grantResponse = await doIssue();
        }

        const sessionInit = await createSetupSession(request, grantResponse.grant, grantResponse.expiresAtUtc);
        const userSessionInit = await setSessionUser(request, username);
        return redirect(
          grantResponse.revocationPending ? "/onboarding?step=services" : "/onboarding?step=usenet",
          mergeResponseHeaders([
            sessionInit,
            userSessionInit,
            csrfHeaders,
            { headers: DEFAULT_NO_CACHE_HEADERS },
          ]),
        );
      }

      case "add-provider": {
        if (!sessionState.hasSetupSession) {
          return createNoCacheResponse(
            {
              mode: "fullstack",
              step: "jellyfin",
              error: "Missing setup session. Complete Jellyfin handoff first.",
            },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        const payload: Record<string, string> = {
          Host: formData.get("provider-host")?.toString() || "",
          Port: formData.get("provider-port")?.toString() || "",
          User: formData.get("provider-user")?.toString() || "",
          MaxConnections: formData.get("provider-max")?.toString() || "",
          ApiKey: "",
          Pass: formData.get("provider-pass")?.toString() || "",
        };

        const error = payload
          ? providerErrorMessage(payload)
          : "Invalid provider entry.";

        if (error) {
          return createNoCacheResponse(
            { mode: "fullstack", step: "usenet", error },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        const provider: SetupUsenetProviderPayload = {
          Host: payload.Host.trim(),
          Port: Number(payload.Port),
          UseSsl: formData.get("provider-ssl")?.toString() === "on",
          User: payload.User.trim(),
          Pass: payload.Pass,
          MaxConnections: Number(payload.MaxConnections),
          Type: Number(formData.get("provider-type") || "1"),
        };

        await addSetupProvider(request, provider);

        return redirect(
          "/onboarding?step=usenet",
          mergeResponseHeaders([csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]),
        );
      }

      case "remove-provider": {
        const providerId = formData.get("provider-id")?.toString() || "";
        await removeSetupProvider(request, providerId);
        return redirect("/onboarding?step=usenet", mergeResponseHeaders([csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]));
      }

      case "add-indexer": {
        if (!sessionState.hasSetupSession) {
          return createNoCacheResponse(
            {
              mode: "fullstack",
              step: "jellyfin",
              error: "Missing setup session. Complete Jellyfin handoff first.",
            },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        const payload: Record<string, string> = {
          Name: formData.get("indexer-name")?.toString() || "",
          Url: formData.get("indexer-url")?.toString() || "",
          ApiKey: formData.get("indexer-apikey")?.toString() || "",
        };

        const error = indexerErrorMessage(payload);
        if (error) {
          return createNoCacheResponse(
            { mode: "fullstack", step: "indexers", error },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        const indexer: SetupIndexerPayload = {
          Name: payload.Name.trim(),
          Url: payload.Url.trim(),
          ApiKey: payload.ApiKey,
          AllowPrivateNetwork: formData.get("indexer-private-network")?.toString() === "on",
        };

        await addSetupIndexer(request, indexer);

        return redirect(
          "/onboarding?step=indexers",
          mergeResponseHeaders([csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]),
        );
      }

      case "remove-indexer": {
        const indexerId = formData.get("indexer-id")?.toString() || "";
        await removeSetupIndexer(request, indexerId);
        return redirect("/onboarding?step=indexers", mergeResponseHeaders([csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]));
      }

      case "configure":
      case "retry": {
        const draft = await getSetupDraftFromSession(request);
        if (!draft.grant) {
          return createNoCacheResponse(
            {
              mode: "fullstack",
              step: "jellyfin",
              error: "Missing setup grant. Restart Jellyfin handoff.",
            },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        let status: SetupStatus;
        if (action === "configure") {
          if (draft.providers.length === 0 || draft.indexers.length === 0) {
            return createNoCacheResponse(
              {
                mode: "fullstack",
                step: draft.providers.length ? "indexers" : "usenet",
                error: "Add providers and indexers before configuring.",
              },
              [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
            );
          }

          status = await backendClient.configureAndRunSetup(
            draft.grant,
            {
              providers: draft.providers,
              indexers: draft.indexers,
            },
            {
              signal: request.signal,
            },
          );
        } else {
          status = await backendClient.retrySetup(draft.grant, {
            signal: request.signal,
          });
        }

        if (status.completed && status.repairRequired) {
          return createNoCacheResponse(
            { mode: "fullstack", step: "repair", error: "Managed-resource repair is still required. Re-authenticate and retry repair." },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        if (status.completed) {
          let revoked = false;
          try {
            await backendClient.revokeSetupGrant(draft.grant, {
              signal: request.signal,
              frontendClientSource: frontendClientSourceFor(request),
            });
            revoked = true;
          } catch (error) {
            const revokeStatus = (error as { status?: number })?.status;
            if (revokeStatus === 401 || revokeStatus === 410) {
              revoked = true;
            }
          }

          if (revoked) {
            const clearSetup = await clearSetupSession(request);
            return redirect(
              "/onboarding?step=ready",
              mergeResponseHeaders([
                clearSetup,
                csrfHeaders,
                { headers: DEFAULT_NO_CACHE_HEADERS },
              ]),
            );
          }

          return createNoCacheResponse(
            {
              mode: "fullstack",
              step: toErrorStep(status),
              error: "Setup completed, but cleanup is pending. Retry logout to refresh.",
            },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        const blocked = mapFailureMessage(status);
        if (blocked) {
          return createNoCacheResponse(
            { mode: "fullstack", step: toErrorStep(status), error: blocked },
            [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
          );
        }

        return redirect("/onboarding?step=configure", mergeResponseHeaders([csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }]));
      }

      default:
        return createNoCacheResponse(
          { mode: "fullstack", step: "services", error: "Unsupported onboarding action." },
          [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
        );
    }
  } catch (error) {
    const status = (error as { status?: number } | undefined)?.status;
    if (status === 429 || status === 503) {
      const retryAfter = (error as { retryAfter?: string } | undefined)?.retryAfter;
      const headers = new Headers(DEFAULT_NO_CACHE_HEADERS);
      if (retryAfter && /^\d{1,4}$/.test(retryAfter)) headers.set("Retry-After", retryAfter);
      return createNoCacheResponse(
        {
          mode: isFullstack ? "fullstack" : "legacy",
          error: status === 429 ? "Too many setup attempts. Please retry later." : "Setup service temporarily unavailable.",
        },
        [csrfHeaders, { headers }],
        status,
      );
    }

    // A configure/retry conflict is a durable lease contention response, not
    // a status result. Preserve the encrypted server-side draft and browser
    // session, keep the wizard on Configure, and expose only the bounded
    // Retry-After value supplied by the backend.
    if (isFullstack && (action === "configure" || action === "retry") && status === 409) {
      const retryAfterSeconds = safeRetryAfterSeconds((error as { retryAfter?: string } | undefined)?.retryAfter);
      const headers = new Headers(DEFAULT_NO_CACHE_HEADERS);
      if (retryAfterSeconds !== undefined) headers.set("Retry-After", String(retryAfterSeconds));
      return createNoCacheResponse(
        {
          mode: "fullstack",
          step: "configure",
          retryAfterSeconds,
          error: retryAfterSeconds === undefined
            ? "Setup is busy. Retry shortly."
            : `Setup is busy. Retry in ${retryAfterSeconds} second${retryAfterSeconds === 1 ? "" : "s"}.`,
        },
        [csrfHeaders, { headers }],
      );
    }

    return createNoCacheResponse(
      {
        mode: isFullstack ? "fullstack" : "legacy",
        step: isFullstack ? toErrorStep(setupStatus || { enabled: false, completed: false, steps: [], services: [], revocationPending: false, repairRequired: false }) : undefined,
        error: getErrorFromMutationError(error),
      },
      [csrfHeaders, { headers: DEFAULT_NO_CACHE_HEADERS }],
    );
  }
}

export default function OnboardingRoute({ loaderData, actionData }: Route.ComponentProps) {
  const typedLoaderData = loaderData as SetupWizardLoaderData;
  const typedActionData = actionData as SetupWizardActionData | undefined;
  const [providerDraft, setProviderDraft] = useState<ProviderDraftInputs>(DEFAULT_PROVIDER_DRAFT);
  const [indexerDraft, setIndexerDraft] = useState<IndexerDraftInputs>(DEFAULT_INDEXER_DRAFT);
  const [jellyfinSetupUrl, setJellyfinSetupUrl] = useState("");

  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  const isLegacyMode = typedLoaderData.mode === "legacy";
  const legacyError =
    typedActionData?.mode === "legacy"
      ? typedActionData.error
      : typedLoaderData.mode === "legacy"
        ? typedLoaderData.error
        : null;

  const fullstackData = typedLoaderData.mode === "fullstack" ? typedLoaderData : null;
  const fullstackActionError =
    typedActionData?.mode === "fullstack"
      ? typedActionData.error
      : typedActionData?.mode === "legacy"
        ? typedActionData.error
        : null;

  const providerSummary = fullstackData?.setupSessionState.providers || [];
  const indexerSummary = fullstackData?.setupSessionState.indexers || [];
  const setupSessionState = fullstackData?.setupSessionState;

  const actionStatus = typedActionData?.mode === "fullstack" ? typedActionData.setupStatus : undefined;
  const setupStatus = actionStatus || fullstackData?.setupStatus || { enabled: false, completed: false, steps: [], services: [], revocationPending: false, repairRequired: false };
  const activeStep = fullstackData
    ? sanitizeWizardStep(
      typedActionData?.mode === "fullstack" ? typedActionData.step : fullstackData.activeStep,
      fullstackData.setupSessionState,
      setupStatus,
    )
    : "services";

  const isSetupCompleted = setupStatus.completed || activeStep === "ready";

  useEffect(() => {
    if (fullstackData?.jellyfinSetupHint) {
      setJellyfinSetupUrl(resolveBrowserJellyfinSetupUrl(fullstackData.jellyfinSetupHint));
    } else {
      setJellyfinSetupUrl(FALLBACK_JELLYFIN_URL);
    }
  }, [fullstackData?.jellyfinSetupHint]);

  const progressSteps = useMemo<SetupStepStatus[]>(() => {
    if (setupStatus.steps.length > 0) {
      return setupStatus.steps;
    }

    return WIZARD_STEPS.map((step) => ({ name: step, state: "pending" }));
  }, [setupStatus]);

  const activeStatusMessage = mapFailureMessage(setupStatus);

  if (isLegacyMode) {
    return (
      <Form className={styles["container"]} method="POST">
        <img className={styles["logo"]} src="/logo.svg" alt="Nzb DAV" />
        <div className={styles["title"]}>Nzb DAV</div>

        <Alert className={styles["alert"]} variant={legacyError ? "danger" : "warning"}>
          {legacyError || "Welcome! Register your admin account."}
        </Alert>

        <BootstrapForm.Control name="username" type="text" placeholder="Username" autoComplete="username" required />
        <BootstrapForm.Control name="password" type="password" placeholder="Password" autoComplete="new-password" required />
        <BootstrapForm.Control name="confirm" type="password" placeholder="Confirm Password" autoComplete="new-password" required />
        <input type="hidden" name="action" value="legacy-register" />
        <input type="hidden" name="csrfToken" value={(typedLoaderData.mode === "legacy" ? typedLoaderData.csrfToken : "")} />

        <Button type="submit" variant="primary" disabled={isSubmitting}>
          {isSubmitting ? "Registering..." : "Register"}
        </Button>
      </Form>
    );
  }

  if (!fullstackData) {
    return null;
  }

  return (
    <main className={styles["container"]}>
      <img className={styles["logo"]} src="/logo.svg" alt="Nzb DAV" />
      <div className={styles["title"]}>Nzb DAV Setup</div>

      {fullstackData.hasRecoveredSession ? (
        <Alert variant="warning" className={styles["alert"]}>
          The setup session expired or was lost. You may need to complete Jellyfin handoff again and re-enter providers/indexers.
        </Alert>
      ) : null}

      {setupStatus.revocationPending ? (
        <Alert variant="warning" className={styles["alert"]}>
          Previous Jellyfin setup-session cleanup is pending. Recover it with the administrator credentials; restarting is not required.
          <Form method="POST" className={styles["action-form"]}>
            <BootstrapForm.Control name="username" type="text" placeholder="Jellyfin administrator username" autoComplete="username" required />
            <BootstrapForm.Control name="password" type="password" placeholder="Jellyfin administrator password" autoComplete="current-password" required />
            <input type="hidden" name="action" value="recover-revocation" />
            <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
            <Button type="submit" variant="warning" disabled={isSubmitting}>
              {isSubmitting ? "Recovering..." : "Recover cleanup"}
            </Button>
          </Form>
        </Alert>
      ) : null}

      {fullstackActionError ? <Alert variant="danger" className={styles["alert"]}>{fullstackActionError}</Alert> : null}

      <div className={styles["progress"]} role="list" aria-label="Setup progress">
        {progressSteps.map((entry, index: number) => (
          <div
            key={`${entry.name}-${index}`}
            className={`${styles["progress-step"]} ${styles[(`state-${entry.state}`) as keyof typeof styles]}`}
            role="listitem"
            aria-current={activeStep === entry.name ? "step" : undefined}
          >
            <span>{entry.name}</span>
            {entry.state === "failed" || entry.state === "warning" ? (
              <small className={styles["status"]}>{(entry.code || entry.reason) === "provider-failed"
                ? "The provider could not be verified."
                : (entry.code || entry.reason) === "indexer-failed"
                  ? "The indexer could not be verified."
                  : (entry.code || entry.reason) === "backend-unavailable"
                    ? "Backend service is unavailable."
                    : (entry.code || entry.reason) === "configuration-failed"
                      ? "The setup configuration was rejected."
                      : (entry.code || entry.reason) === "plugin-mismatch"
                        ? "The NZBDAV Jellyfin plugin configuration was not retained."
                        : (entry.code || entry.reason) === "library-collision"
                          ? "Jellyfin library paths conflict."
                          : "Setup could not complete; retry the failed step."}</small>
            ) : null}
          </div>
        ))}
      </div>

      <section className={styles["wizard-card"]}>
        {activeStep === "services" && (
          <>
            <h2>Services</h2>
            <div role="status" aria-label="Service readiness">
              {(setupStatus.services.length > 0
                ? setupStatus.services
                : (["jellyfin", "sonarr", "radarr", "prowlarr", "provider"].map((name) => ({ name, ready: false })) as SetupServiceStatus[]))
                .map((service) => (
                  <p key={service.name}>{service.name}: {service.ready ? "Ready" : service.reason === "setup-run-busy" ? "Checking (setup run busy)" : "Not ready"}{!service.ready && service.reason ? ` (${service.reason})` : ""}</p>
                ))}
            </div>
            <p>Open Jellyfin to complete setup and create an admin account, then return here and continue.</p>
            <p>
              Jellyfin setup URL: {jellyfinSetupUrl ? <a href={jellyfinSetupUrl}>{jellyfinSetupUrl}</a> : <span>Detecting...</span>}
            </p>
            {setupStatus.completed ? (
              <Form method="POST" className={styles["action-form"]}>
                <input type="hidden" name="action" value="verify-services" />
                <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
                <Button type="submit" variant="secondary" disabled={isSubmitting}>
                  {isSubmitting ? "Verifying services..." : "Verify services and discover repairs"}
                </Button>
              </Form>
            ) : null}
            <Button as="a" href="/onboarding?step=jellyfin" variant="outline-primary">
              Continue
            </Button>
          </>
        )}

        {activeStep === "jellyfin" && (
          <Form method="POST" className={styles["inner-form"]}>
            <h2>Jellyfin Handoff</h2>
            {setupSessionState?.hasSetupSession ? (
              <>
                <Alert variant="success">Handoff complete. Continue to providers.</Alert>
                <Button as="a" href="/onboarding?step=usenet" variant="outline-primary">
                  Continue
                </Button>
              </>
            ) : (
              <>
                <BootstrapForm.Label htmlFor="setup-jellyfin-username">Jellyfin administrator username</BootstrapForm.Label>
                <BootstrapForm.Control
                  id="setup-jellyfin-username"
                  name="username"
                  type="text"
                  autoComplete="username"
                  required
                />
                <BootstrapForm.Label htmlFor="setup-jellyfin-password">Password</BootstrapForm.Label>
                <BootstrapForm.Control
                  id="setup-jellyfin-password"
                  name="password"
                  type="password"
                  autoComplete="current-password"
                  required
                />
                <input type="hidden" name="action" value="handoff" />
                <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
                <Button type="submit" disabled={isSubmitting}>
                  {isSubmitting ? "Signing in..." : "Authenticate with Jellyfin"}
                </Button>
              </>
            )}
          </Form>
        )}

        {activeStep === "usenet" && (
          <>
            <h2>Usenet providers</h2>
            <Form method="POST" className={styles["inner-form"]}>
              <BootstrapForm.Label htmlFor="provider-host">Host</BootstrapForm.Label>
              <BootstrapForm.Control
                id="provider-host"
                name="provider-host"
                value={providerDraft.Host}
                onChange={(event) => updateFromInput(event, (value) =>
                  setProviderDraft((current) => ({ ...current, Host: value })), "value")}
              />
              <BootstrapForm.Label htmlFor="provider-port">Port</BootstrapForm.Label>
              <BootstrapForm.Control
                id="provider-port"
                name="provider-port"
                type="number"
                value={providerDraft.Port}
                onChange={(event) => updateFromInput(event, (value) =>
                  setProviderDraft((current) => ({ ...current, Port: value })), "value")}
              />
              <BootstrapForm.Label htmlFor="provider-user">Username</BootstrapForm.Label>
              <BootstrapForm.Control
                id="provider-user"
                name="provider-user"
                value={providerDraft.User}
                onChange={(event) => updateFromInput(event, (value) =>
                  setProviderDraft((current) => ({ ...current, User: value })), "value")}
              />
              <BootstrapForm.Label htmlFor="provider-pass">Password</BootstrapForm.Label>
              <BootstrapForm.Control id="provider-pass" name="provider-pass" type="password" autoComplete="new-password" />
              <BootstrapForm.Label htmlFor="provider-max">Max Connections</BootstrapForm.Label>
              <BootstrapForm.Control
                id="provider-max"
                name="provider-max"
                type="number"
                value={providerDraft.MaxConnections}
                onChange={(event) => updateFromInput(event, (value) =>
                  setProviderDraft((current) => ({ ...current, MaxConnections: value })), "value")}
              />
              <BootstrapForm.Label htmlFor="provider-type">Type</BootstrapForm.Label>
              <BootstrapForm.Select
                id="provider-type"
                name="provider-type"
                value={providerDraft.Type}
                onChange={(event) => updateFromInput(event, (value) =>
                  setProviderDraft((current) => ({ ...current, Type: Number.parseInt(value, 10) })), "value")}
              >
                <option value={1}>Pooled</option>
                <option value={2}>Backup &amp; Health Checks</option>
                <option value={3}>Backup Only</option>
              </BootstrapForm.Select>
              <BootstrapForm.Check
                id="provider-ssl"
                label="Use SSL/TLS"
                name="provider-ssl"
                type="checkbox"
                checked={providerDraft.UseSsl}
                onChange={(event) => updateFromInput(event, (checked) =>
                  setProviderDraft((current) => ({ ...current, UseSsl: checked })), "checked")}
              />
              <input type="hidden" name="action" value="add-provider" />
              <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
              <Button type="submit" disabled={isSubmitting || providerSummary.length >= MAX_PROVIDER_FIELDS}>
                Add Provider
              </Button>
            </Form>

            {providerSummary.length > 0 && (
              <div className={styles["item-list"]}>
                {providerSummary.map((provider: SetupDraftSummaryItem) => (
                  <div key={provider.id} className={styles["item-card"]}>
                    <div className={styles["item-card-header"]}>
                      <strong>{provider.host}:{provider.port}</strong>
                      <Form method="POST">
                        <input type="hidden" name="provider-id" value={provider.id} />
                        <input type="hidden" name="action" value="remove-provider" />
                        <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
                        <Button size="sm" variant="outline-danger" type="submit">
                          Remove
                        </Button>
                      </Form>
                    </div>
                    <div className={styles["item-card-meta"]}>{provider.user}</div>
                    <div className={styles["item-card-meta"]}>Connections: {provider.maxConnections}</div>
                  </div>
                ))}
              </div>
            )}

            <Button as="a" href="/onboarding?step=indexers" variant="outline-primary" disabled={providerSummary.length === 0}>
              Continue
            </Button>
          </>
        )}

        {activeStep === "indexers" && (
          <>
            <h2>Indexers</h2>
            <Form method="POST" className={styles["inner-form"]}>
              <BootstrapForm.Label htmlFor="indexer-name">Name</BootstrapForm.Label>
              <BootstrapForm.Control
                id="indexer-name"
                name="indexer-name"
                value={indexerDraft.Name}
                onChange={(event) => updateFromInput(event, (value) =>
                  setIndexerDraft((current) => ({ ...current, Name: value })), "value")}
              />
              <BootstrapForm.Label htmlFor="indexer-url">URL</BootstrapForm.Label>
              <BootstrapForm.Control
                id="indexer-url"
                name="indexer-url"
                value={indexerDraft.Url}
                onChange={(event) => updateFromInput(event, (value) =>
                  setIndexerDraft((current) => ({ ...current, Url: value })), "value")}
              />
              <BootstrapForm.Label htmlFor="indexer-apikey">API Key</BootstrapForm.Label>
              <BootstrapForm.Control
                id="indexer-apikey"
                name="indexer-apikey"
                type="password"
                autoComplete="new-password"
              />
              <BootstrapForm.Check
                id="indexer-private-network"
                name="indexer-private-network"
                label="Allow private network address (explicit opt-in)"
                type="checkbox"
              />
              <input type="hidden" name="action" value="add-indexer" />
              <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
              <Button type="submit" disabled={isSubmitting || indexerSummary.length >= MAX_INDEXER_FIELDS}>
                Add Indexer
              </Button>
            </Form>

            {indexerSummary.length > 0 && (
              <div className={styles["item-list"]}>
                {indexerSummary.map((indexer: SetupDraftIndexerSummary) => (
                  <div key={indexer.id} className={styles["item-card"]}>
                    <div className={styles["item-card-header"]}>
                      <strong>{indexer.name}</strong>
                      <Form method="POST">
                        <input type="hidden" name="indexer-id" value={indexer.id} />
                        <input type="hidden" name="action" value="remove-indexer" />
                        <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
                        <Button size="sm" variant="outline-danger" type="submit">
                          Remove
                        </Button>
                      </Form>
                    </div>
                    <div className={styles["item-card-meta"]}>{indexer.url}</div>
                    {indexer.allowPrivateNetwork && <div className={styles["item-card-meta"]}>Private network access enabled</div>}
                  </div>
                ))}
              </div>
            )}

            <Button as="a" href="/onboarding?step=configure" variant="outline-primary" disabled={indexerSummary.length === 0}>
              Continue
            </Button>
          </>
        )}

        {activeStep === "configure" && (
          <>
            <h2>Configure</h2>
            <p>Providers: {providerSummary.length}, Indexers: {indexerSummary.length}</p>

            {isSetupCompleted ? <Alert variant="info">Setup completed. Open NZBDAV.</Alert> : null}

            {activeStatusMessage ? <Alert variant="warning">{activeStatusMessage}</Alert> : null}

            <Form method="POST" className={styles["action-form"]}>
              <input type="hidden" name="action" value="configure" />
              <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
              <Button type="submit" disabled={isSubmitting || !setupSessionState?.hasSetupSession || providerSummary.length === 0 || indexerSummary.length === 0}>
                {isSubmitting ? "Running setup..." : "Run configuration"}
              </Button>
            </Form>

            <Form method="POST" className={styles["action-form"]}>
              <input type="hidden" name="action" value="retry" />
              <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
              <Button type="submit" variant="secondary" disabled={isSubmitting || !setupSessionState?.hasSetupSession}>
                Retry configuration
              </Button>
            </Form>
          </>
        )}

        {activeStep === "repair" && (
          <>
            <h2>Repair completed setup</h2>
            <p>Setup is complete, but live health checks found stale managed wiring. Re-authenticate explicitly to repair only the managed resources.</p>
            <Form method="POST" className={styles["inner-form"]}>
              <input type="hidden" name="action" value="repair-handoff" />
              <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
              <BootstrapForm.Control name="username" type="text" placeholder="Jellyfin administrator username" autoComplete="username" required />
              <BootstrapForm.Control name="password" type="password" placeholder="Jellyfin administrator password" autoComplete="current-password" required />
              <Button type="submit" variant="primary" disabled={isSubmitting}>Authenticate for repair</Button>
            </Form>
            {setupSessionState?.hasSetupSession ? (
              <Form method="POST" className={styles["action-form"]}>
                <input type="hidden" name="action" value="repair" />
                <input type="hidden" name="csrfToken" value={fullstackData.csrfToken} />
                <Button type="submit" variant="secondary" disabled={isSubmitting}>Retry managed repair</Button>
              </Form>
            ) : null}
          </>
        )}
        {activeStep === "ready" && (
          <>
            <h2>Ready</h2>
            <p>
              Full-stack setup completed. Open NZBDAV when you are ready.
            </p>
            <ul className={styles["ready-list"]}>
              <li>
                <a href={jellyfinSetupUrl || FALLBACK_JELLYFIN_URL} target="_blank" rel="noreferrer">
                  Open Jellyfin
                </a>
              </li>
              <li>
                <a href="/" target="_blank" rel="noreferrer">
                  Open NZBDAV
                </a>
              </li>
            </ul>
          </>
        )}
      </section>
    </main>
  );
}

import {
  Links,
  Meta,
  Outlet,
  redirect,
  Scripts,
  ScrollRestoration,
  useLocation,
  useNavigation,
} from "react-router";

import 'bootstrap/dist/css/bootstrap.min.css';
import "./app.css";
import type { Route } from "./+types/root";
import { IS_FRONTEND_AUTH_DISABLED, isAuthenticated } from "~/auth/authentication.server";
import { TopNavigation } from "./routes/_index/components/top-navigation/top-navigation";
import { LeftNavigation } from "./routes/_index/components/left-navigation/left-navigation";
import { getCsrfToken } from "./onboarding/onboarding-csrf.server";
import { PageLayout } from "./routes/_index/components/page-layout/page-layout";
import { Loading } from "./routes/_index/components/loading/loading";
import { DEFAULT_NO_CACHE_HEADERS } from "./onboarding/onboarding-request.server";
import { backendClient } from "./clients/backend-client.server";

const PRIVATE_NO_CACHE_HEADERS: HeadersInit = {
  ...DEFAULT_NO_CACHE_HEADERS,
  "Cache-Control": `private, ${(DEFAULT_NO_CACHE_HEADERS as Record<string, string>)["Cache-Control"]}`,
};

function withNoCache(headers?: HeadersInit): ResponseInit {
  const merged = new Headers(headers);
  for (const [name, value] of new Headers(PRIVATE_NO_CACHE_HEADERS).entries()) {
    merged.append(name, value);
  }
  return { headers: merged };
}

const withAuthRedirectHeaders = (): ResponseInit => ({
  headers: new Headers(PRIVATE_NO_CACHE_HEADERS),
});

export async function loader({ request }: Route.LoaderArgs) {
  // unauthenticated routes
  let path = new URL(request.url).pathname;
  if (path === "/login") return { useLayout: false };
  if (path === "/onboarding") return { useLayout: false };

  // ensure all other routes are authenticated
  try {
    if (!await isAuthenticated(request)) return redirect("/login", withAuthRedirectHeaders());
  } catch {
    return redirect("/login", withAuthRedirectHeaders());
  }

  const csrf = await getCsrfToken(request);
  let resumeSetup = false;
  try {
    const setupStatus = await backendClient.getSetupStatus();
    resumeSetup = setupStatus.enabled && !setupStatus.completed;
  } catch {
    // Navigation must remain available when setup status is temporarily down.
  }
  return Response.json(
    {
      useLayout: true,
      resumeSetup,
      version: process.env.NZBDAV_VERSION,
      isFrontendAuthDisabled: IS_FRONTEND_AUTH_DISABLED,
      csrfToken: csrf.token,
    },
    withNoCache(csrf.headers),
  );
}


export function Layout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" data-bs-theme="dark">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <link rel="icon" href="/logo.svg" />
        <Meta />
        <Links />
      </head>
      <body>
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export default function App({ loaderData }: Route.ComponentProps) {
  const {
    useLayout,
    version,
    isFrontendAuthDisabled,
    csrfToken,
    resumeSetup,
  } = loaderData as {
    useLayout: boolean,
    version?: string,
    isFrontendAuthDisabled?: boolean,
    csrfToken?: string,
    resumeSetup?: boolean,
  };
  const location = useLocation();
  const navigation = useNavigation();
  const isNavigating = Boolean(navigation.location);

  // display loading animiation during top-level page transitions,
  // but allow the `/explore` page to handle it's own loading screen.
  const isCurrentExplorePage = location.pathname.startsWith("/explore");
  const isNextExplorePage = navigation.location?.pathname?.startsWith("/explore");
  const showLoading = isNavigating && !(isCurrentExplorePage && isNextExplorePage);

  if (useLayout) {
    return (
      <>
      <meta name="csrf-token" content={csrfToken || ""} />
      <PageLayout
        topNavComponent={TopNavigation}
        bodyChild={showLoading ? <Loading /> : <Outlet />}
        leftNavChild={
          <LeftNavigation
            version={version}
            isFrontendAuthDisabled={isFrontendAuthDisabled}
            csrfToken={csrfToken}
            resumeSetup={resumeSetup}
          />
        } />
      </>
    );
  }

  return <Outlet />;
}
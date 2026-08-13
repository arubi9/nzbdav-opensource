export const FALLBACK_JELLYFIN_URL = "http://localhost:8096";

export type JellyfinSetupLinkHint = {
  explicitPublicUrl: string | null;
  publicPort: number;
};

export const DEFAULT_JELLYFIN_PORT = 8096;
const MAX_TCP_PORT = 65535;

const trimPort = (value: string | undefined | null): number => {
  const port = Number.parseInt(value || "", 10);
  if (!Number.isInteger(port) || port < 1 || port > MAX_TCP_PORT) {
    return DEFAULT_JELLYFIN_PORT;
  }

  return port;
};

const parsePublicUrl = (value: string | undefined | null): string | null => {
  if (!value) return null;

  try {
    const parsed = new URL(value);
    if (!/^https?:$/i.test(parsed.protocol)) return null;
    if (parsed.username || parsed.password) return null;
    if (parsed.search || parsed.hash) return null;
    const pathname = parsed.pathname === "/" ? "" : parsed.pathname.replace(/\/+$/, "");

    return pathname === "" ? parsed.origin : `${parsed.origin}${pathname}`;
  } catch {
    return null;
  }
};

export const getJellyfinSetupLinkHint = (): JellyfinSetupLinkHint => {
  const explicit = parsePublicUrl(process?.env?.JELLYFIN_PUBLIC_URL || process?.env?.PUBLIC_JELLYFIN_URL);
  const publicPort = trimPort(
    process?.env?.JELLYFIN_PUBLIC_PORT ||
      process?.env?.PUBLIC_JELLYFIN_PORT ||
      process?.env?.JELLYFIN_PORT,
  );

  return {
    explicitPublicUrl: explicit,
    publicPort,
  };
};

const formatHostname = (hostname: string): string => {
  if (!hostname) return "localhost";
  const containsColon = hostname.includes(":");
  if (containsColon && !hostname.startsWith("[") && !hostname.endsWith("]")) {
    return `[${hostname}]`;
  }

  return hostname;
};

export const resolveBrowserJellyfinSetupUrl = (hint: JellyfinSetupLinkHint): string => {
  if (typeof window === "undefined") {
    return "";
  }

  if (hint.explicitPublicUrl) {
    return hint.explicitPublicUrl;
  }

  const location = window.location;
  const protocol = location.protocol || "http:";
  const host = formatHostname(location.hostname || "localhost");

  return `${protocol}//${host}:${hint.publicPort}`;
};

import net from "node:net";

export type StartupEnvironment = Record<string, string | undefined>;

const parsePort = (name: string, value: string | undefined, fallback: number): number => {
  const raw = value ?? String(fallback);
  if (!/^\d+$/.test(raw)) throw new Error(`${name} must be a valid port.`);
  const port = Number(raw);
  if (!Number.isSafeInteger(port) || port < 1 || port > 65535) throw new Error(`${name} must be a valid port.`);
  return port;
};

const parsePositiveInteger = (name: string, value: string | undefined, fallback: number): number => {
  const raw = value ?? String(fallback);
  const parsed = Number(raw);
  if (!/^\d+$/.test(raw) || !Number.isSafeInteger(parsed) || parsed < 1) {
    throw new Error(`${name} must be a positive integer.`);
  }
  return parsed;
};

const parseBoolean = (name: string, value: string | undefined, fallback: boolean): boolean => {
  if (value === undefined) return fallback;
  const normalized = value.toLowerCase();
  if (normalized !== "true" && normalized !== "false") {
    throw new Error(`${name} must be true or false.`);
  }
  return normalized === "true";
};

const isStrongSecret = (value: string | undefined): boolean => {
  // Deployment secrets are expected to be generated random values, not short
  // development placeholders.  Length is deliberately the only property
  // inspected so valid random base64/hex secrets are both accepted.
  return typeof value === "string" && value.trim() === value && value.length >= 32;
};

/** The backend websocket has no safe anonymous fallback key. */
export function validateFrontendBackendApiKey(env: StartupEnvironment = process.env): string {
  const value = env.FRONTEND_BACKEND_API_KEY;
  if (typeof value !== "string" || value.length === 0 || value.trim() !== value || /[\u0000-\u001f\u007f]/.test(value)) {
    throw new Error("FRONTEND_BACKEND_API_KEY must be explicitly configured.");
  }
  if (env.NODE_ENV === "production" && !isStrongSecret(value)) {
    throw new Error("FRONTEND_BACKEND_API_KEY must be a strong secret in production.");
  }
  return value;
}

export const canonicalOrigin = (name: string, value: string): string => {
  let parsed: URL;
  try { parsed = new URL(value); } catch { throw new Error(`${name} must be a canonical http(s) origin.`); }
  if (!/^https?:$/.test(parsed.protocol) || parsed.username || parsed.password
    || parsed.pathname !== "/" || parsed.search || parsed.hash || parsed.origin !== value.replace(/\/$/, "")) {
    throw new Error(`${name} must be a canonical http(s) origin.`);
  }
  return parsed.origin;
};

type RequestHeaders = Record<string, string | string[] | undefined>;
const FORWARDED_HEADER_NAMES = [
  "forwarded", "x-forwarded-for", "x-forwarded-host", "x-forwarded-proto", "x-forwarded-port", "x-real-ip",
] as const;

const strictAuthority = (name: string, value: string): string => {
  if (!value || value !== value.trim() || /[\u0000-\u001f\u007f\s\/,?#@]/.test(value)) {
    throw new Error(`${name} must be an exact host[:port] authority.`);
  }
  const match = /^(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:]+\])(?::([0-9]{1,5}))?$/.exec(value);
  if (!match || (match[1] !== undefined && (Number(match[1]) < 1 || Number(match[1]) > 65535))) {
    throw new Error(`${name} must be an exact host[:port] authority.`);
  }
  try {
    const parsed = new URL(`http://${value}`);
    if (parsed.username || parsed.password || parsed.pathname !== "/" || parsed.search || parsed.hash || parsed.host !== value) {
      throw new Error(`${name} must be an exact host[:port] authority.`);
    }
  } catch {
    throw new Error(`${name} must be an exact host[:port] authority.`);
  }
  return value;
};

/** Strict browser/proxy authority: no path slash, credentials, query, hash, or controls. */
export const strictHttpOrigin = (name: string, value: string): string => {
  if (!value || value !== value.trim() || /[\u0000-\u001f\u007f]/.test(value)) {
    throw new Error(`${name} must be an exact http(s) origin.`);
  }
  let parsed: URL;
  try { parsed = new URL(value); } catch { throw new Error(`${name} must be an exact http(s) origin.`); }
  if (!/^https?:$/.test(parsed.protocol) || parsed.username || parsed.password || parsed.pathname !== "/"
    || parsed.search || parsed.hash || parsed.origin !== value) {
    throw new Error(`${name} must be an exact http(s) origin.`);
  }
  strictAuthority(name, parsed.host);
  return parsed.origin;
};

const strictHeader = (headers: RequestHeaders, name: string): string | undefined => {
  const raw = headers[name];
  if (raw === undefined) return undefined;
  if (typeof raw !== "string" || raw.length === 0 || raw.includes(",") || /[\u0000-\u001f\u007f]/.test(raw)) {
    throw new Error(`Invalid ${name} header.`);
  }
  return raw;
};

const forwardedField = (value: string): Map<string, string> => {
  const fields = new Map<string, string>();
  for (const part of value.split(";")) {
    const separator = part.indexOf("=");
    if (separator <= 0) throw new Error("Invalid Forwarded header.");
    const key = part.slice(0, separator).trim().toLowerCase();
    const fieldValue = part.slice(separator + 1).trim();
    if (!/^[a-z][a-z0-9_-]*$/.test(key) || !fieldValue || fields.has(key)) throw new Error("Invalid Forwarded header.");
    fields.set(key, fieldValue);
  }
  return fields;
};

/** Resolve the same strict external authority for HTTP and WebSocket requests. */
export const externalOriginFromHeaders = (
  headers: RequestHeaders,
  remoteAddress: string | undefined,
  env: StartupEnvironment = process.env,
  encrypted = false,
): string => {
  const forwarded = new Map<string, string | undefined>();
  for (const name of FORWARDED_HEADER_NAMES) forwarded.set(name, strictHeader(headers, name));
  const hasForwarded = [...forwarded.values()].some(value => value !== undefined);
  const trusted = isTrustedProxyAddress(remoteAddress, env);
  if (hasForwarded && !trusted) throw new Error("Forwarded headers are not accepted from this peer.");

  const requestHost = strictAuthority("Host", strictHeader(headers, "host") || "");
  let protocol = encrypted ? "https" : "http";
  let host = requestHost;
  if (trusted && forwarded.get("forwarded")) {
    if (FORWARDED_HEADER_NAMES.some(name => name !== "forwarded" && forwarded.get(name) !== undefined)) {
      throw new Error("Conflicting forwarded headers.");
    }
    const fields = forwardedField(forwarded.get("forwarded")!);
    const forwardedProto = fields.get("proto");
    const forwardedHost = fields.get("host");
    if (!forwardedProto || !/^https?$/.test(forwardedProto.toLowerCase()) || !forwardedHost) {
      throw new Error("Invalid Forwarded header.");
    }
    protocol = forwardedProto.toLowerCase();
    host = strictAuthority("Forwarded host", forwardedHost);
  } else if (trusted) {
    protocol = (forwarded.get("x-forwarded-proto") || protocol).toLowerCase();
    if (!/^https?$/.test(protocol)) throw new Error("Invalid X-Forwarded-Proto header.");
    host = forwarded.get("x-forwarded-host") ? strictAuthority("X-Forwarded-Host", forwarded.get("x-forwarded-host")!) : host;
    const port = forwarded.get("x-forwarded-port");
    if (port !== undefined) {
      if (!/^[0-9]{1,5}$/.test(port) || Number(port) < 1 || Number(port) > 65535 || /:\\d+$/.test(host)) {
        throw new Error("Invalid X-Forwarded-Port header.");
      }
      host = strictAuthority("X-Forwarded-Host", `${host}:${port}`);
    }
  }

  const origin = strictHttpOrigin("Request origin", `${protocol}://${host}`);
  const configured = env.PUBLIC_ORIGIN?.trim()
    ? strictHttpOrigin("PUBLIC_ORIGIN", env.PUBLIC_ORIGIN.trim())
    : undefined;
  const forwardedAuthority = forwarded.get("forwarded") !== undefined
    || forwarded.get("x-forwarded-host") !== undefined
    || forwarded.get("x-forwarded-proto") !== undefined
    || forwarded.get("x-forwarded-port") !== undefined;
  // PUBLIC_ORIGIN is authoritative for a direct TLS listener. When a trusted
  // proxy supplies authority headers, however, those headers must agree with
  // it exactly rather than silently overriding a mismatch.
  if (configured && forwardedAuthority && origin !== configured) throw new Error("Request origin does not match PUBLIC_ORIGIN.");
  return configured || origin;
};

export type ValidatedCookieConfig = {
  secureCookies: boolean;
  publicOrigin?: string;
  trustedProxies: string[];
  bindAddress: string;
  insecureDevCookies: boolean;
};

export type ValidatedStartupConfig = ValidatedCookieConfig & {
  port: number;
  backendTimeoutMs: number;
  listenAddress: string;
};

const normalizeAddress = (address: string): string => {
  const trimmed = address.trim();
  if (trimmed.startsWith("[") && trimmed.endsWith("]")) return trimmed.slice(1, -1);
  return trimmed;
};

const validateBindAddress = (value: string): { address: string; loopback: boolean } => {
  if (!value || value !== value.trim() || /\s/.test(value)) throw new Error("BIND_ADDRESS must be a valid IP address or localhost.");
  if (value.includes("[") || value.includes("]")) throw new Error("BIND_ADDRESS must be a valid IP address or localhost.");
  const address = normalizeAddress(value);
  const version = net.isIP(address);
  if (version === 0 && address !== "localhost") throw new Error("BIND_ADDRESS must be a valid IP address or localhost.");
  if (address === "localhost") return { address, loopback: true };
  return { address, loopback: address === "127.0.0.1" || address === "::1" };
};

const netIpVersion = (address: string): 4 | 6 | undefined => {
  const version = net.isIP(address);
  return version === 4 || version === 6 ? version : undefined;
};

const ipv6ToBigInt = (address: string): bigint | undefined => {
  const pieces = address.split("::");
  if (pieces.length > 2) return undefined;
  const left = pieces[0] ? pieces[0].split(":") : [];
  const right = pieces.length === 2 && pieces[1] ? pieces[1].split(":") : [];
  if (left.some(piece => !/^[0-9a-f]{1,4}$/i.test(piece)) || right.some(piece => !/^[0-9a-f]{1,4}$/i.test(piece))) return undefined;
  if (pieces.length === 1 && left.length !== 8) return undefined;
  if (pieces.length === 2 && left.length + right.length >= 8) return undefined;
  const expanded = [...left, ...Array(8 - left.length - right.length).fill("0"), ...right];
  return expanded.reduce((value, piece) => (value << 16n) | BigInt(`0x${piece || "0"}`), 0n);
};

const ipv4FromBigInt = (value: bigint): string => [24n, 16n, 8n, 0n]
  .map(shift => Number((value >> shift) & 255n)).join(".");

const normalizeIp = (address: string): { address: string; version: 4 | 6 } | undefined => {
  const normalized = normalizeAddress(address);
  if (normalized.includes("%")) return undefined;
  const version = netIpVersion(normalized);
  if (!version) return undefined;
  if (version === 4) return { address: normalized, version };

  // Node accepts dotted tails in arbitrary IPv6 addresses, but BigInt does
  // not. Reject those unsupported forms rather than allowing a request-time
  // exception. IPv4-mapped dotted and hexadecimal forms are both canonicalized.
  if (normalized.includes(".")) {
    const mapped = normalized.match(/^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i);
    if (!mapped || netIpVersion(mapped[1]) !== 4) return undefined;
    return { address: mapped[1], version: 4 };
  }
  const numeric = ipv6ToBigInt(normalized);
  if (numeric === undefined) return undefined;
  if ((numeric >> 32n) === 0xffffn) return { address: ipv4FromBigInt(numeric & 0xffffffffn), version: 4 };
  return { address: normalized, version };
};

const validProxyEntry = (entry: string): boolean => {
  const parts = entry.split("/");
  if (parts.length > 2) return false;
  const parsed = normalizeIp(parts[0]);
  if (!parsed) return false;
  if (parts[1] === undefined) return true;
  return /^\d+$/.test(parts[1]) && Number(parts[1]) <= (parsed.version === 4 ? 32 : 128);
};

const ipToBigInt = (address: string, version: 4 | 6): bigint => {
  if (version === 4) return address.split(".").reduce((value, octet) => (value << 8n) | BigInt(octet), 0n);
  const pieces = address.split("::");
  const left = pieces[0] ? pieces[0].split(":") : [];
  const right = pieces[1] ? pieces[1].split(":") : [];
  const expanded = [...left, ...Array(8 - left.length - right.length).fill("0"), ...right];
  return expanded.reduce((value, piece) => (value << 16n) | BigInt(`0x${piece || "0"}`), 0n);
};

export const trustedProxyEntries = (env: StartupEnvironment = process.env): string[] => {
  const raw = env.TRUSTED_PROXIES ?? env.TRUSTED_PROXY ?? "";
  if (!raw.trim()) return [];
  const entries = raw.split(",").map((entry) => normalizeAddress(entry.trim())).filter(Boolean).map((entry) => {
    const [address, prefix] = entry.split("/");
    const parsed = normalizeIp(address);
    return parsed ? `${parsed.address}${prefix === undefined ? "" : `/${prefix}`}` : entry;
  });
  if (entries.some((entry) => !validProxyEntry(entry))) throw new Error("TRUSTED_PROXIES must contain IP addresses or CIDR ranges.");
  return entries;
};

export const isTrustedProxyAddress = (address: string | undefined, env: StartupEnvironment = process.env): boolean => {
  if (!address) return false;
  try {
    const parsedAddress = normalizeIp(address);
    if (!parsedAddress) return false;
    const proxyEntries = trustedProxyEntries(env);
    return proxyEntries.some((entry) => {
    const [network, prefixText] = entry.split("/");
    const parsedNetwork = normalizeIp(network);
    if (!parsedNetwork || parsedNetwork.version !== parsedAddress.version) return false;
    const prefix = prefixText === undefined ? (parsedAddress.version === 4 ? 32 : 128) : Number(prefixText);
    const bits = parsedAddress.version === 4 ? 32 : 128;
    const shift = BigInt(bits - prefix);
    return (ipToBigInt(parsedAddress.address, parsedAddress.version) >> shift)
      === (ipToBigInt(parsedNetwork.address, parsedNetwork.version) >> shift);
    });
  } catch {
    // Proxy headers are request input. A malformed address must simply not be
    // trusted, never make request handling throw.
    return false;
  }
};

/** The one source of truth for cookie/origin policy used by every server module. */
export function validateCookieConfig(env: StartupEnvironment = process.env): ValidatedCookieConfig {
  const nodeEnv = env.NODE_ENV ?? "development";
  if (nodeEnv !== "development" && nodeEnv !== "test" && nodeEnv !== "production") {
    throw new Error("NODE_ENV must be development, test, or production.");
  }

  const optionalOrigin = (value: string | undefined): string | undefined => {
    const trimmed = value?.trim();
    return trimmed ? canonicalOrigin("PUBLIC_ORIGIN", trimmed) : undefined;
  };

  const publicOrigin = optionalOrigin(env.PUBLIC_ORIGIN);
  const production = nodeEnv === "production";
  const secureCookies = parseBoolean("SECURE_COOKIES", env.SECURE_COOKIES, production);
  const bind = validateBindAddress(env.BIND_ADDRESS ?? "127.0.0.1");
  const bindAddress = bind.address;
  const loopback = bind.loopback;
  const insecureDevCookieValue = env.NZBDAV_INSECURE_DEV_COOKIES;
  const insecureDevCookies = insecureDevCookieValue === undefined
    ? false
    : insecureDevCookieValue === "true"
      ? true
      : insecureDevCookieValue === "false"
        ? false
        : (() => { throw new Error("NZBDAV_INSECURE_DEV_COOKIES must be true or false."); })();
  if (publicOrigin?.startsWith("https://") && !secureCookies) {
    throw new Error("SECURE_COOKIES=false is unsafe for an HTTPS PUBLIC_ORIGIN.");
  }
  if (!loopback && !secureCookies && !insecureDevCookies) {
    throw new Error("Non-loopback HTTP requires NZBDAV_INSECURE_DEV_COOKIES=true acknowledgement or Secure cookies.");
  }
  return { secureCookies, publicOrigin, trustedProxies: trustedProxyEntries(env), bindAddress, insecureDevCookies };
}

export function validateStartupConfig(env: StartupEnvironment = process.env): ValidatedStartupConfig {
  const nodeEnv = env.NODE_ENV ?? "development";
  if (nodeEnv !== "development" && nodeEnv !== "test" && nodeEnv !== "production") {
    throw new Error("NODE_ENV must be development, test, or production.");
  }

  const production = nodeEnv === "production";
  if (production && !isStrongSecret(env.SESSION_KEY)) {
    throw new Error("SESSION_KEY must be a strong secret in production.");
  }
  validateFrontendBackendApiKey(env);

  const cookieConfig = validateCookieConfig(env);
  for (const [name, value] of [
    ["PUBLIC_URL", env.PUBLIC_URL],
    ["FRONTEND_PUBLIC_URL", env.FRONTEND_PUBLIC_URL],
    ["JELLYFIN_PUBLIC_URL", env.JELLYFIN_PUBLIC_URL],
  ] as const) {
    const trimmed = value?.trim();
    if (trimmed) canonicalOrigin(name, trimmed);
  }

  const port = parsePort("PORT", env.PORT, 3000);
  const backendTimeoutMs = parsePositiveInteger(
    "FRONTEND_BACKEND_REQUEST_TIMEOUT_MS",
    env.FRONTEND_BACKEND_REQUEST_TIMEOUT_MS,
    12000,
  );
  const listenAddress = validateBindAddress(env.FRONTEND_LISTEN_ADDRESS ?? "0.0.0.0").address;

  trustedProxyEntries(env);

  if (env.BACKEND_URL) {
    let backend: URL;
    try { backend = new URL(env.BACKEND_URL); } catch { throw new Error("BACKEND_URL must be a valid URL."); }
    if (!/^https?:$/.test(backend.protocol) || backend.username || backend.password
      || backend.search || backend.hash) {
      throw new Error("BACKEND_URL must be a valid http(s) URL.");
    }
  }

  return { port, backendTimeoutMs, listenAddress, ...cookieConfig };
}

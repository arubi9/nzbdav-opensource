import { timingSafeEqual } from "node:crypto";

export const MAX_ONBOARDING_FORM_BYTES = 32 * 1024;
export const MAX_ONBOARDING_FORM_FIELD_COUNT = 64;
export const MAX_ONBOARDING_FIELD_NAME_BYTES = 64;
export const MAX_ONBOARDING_FIELD_VALUE_BYTES = 4096;

export type ParsedOnboardingForm = URLSearchParams;

export type OnboardingRequestParseError = {
  kind: "bad-request";
  message: string;
};

const toText = (bytes: number): string => {
  return new Intl.NumberFormat("en-US").format(bytes);
};

function createParseError(message: string): Error & OnboardingRequestParseError {
  return Object.assign(new Error(message), { kind: "bad-request", message } as OnboardingRequestParseError);
}

export const REQUEST_BODY_DEADLINE_MS = 15_000;

/** Read a request stream with both a byte ceiling and an application deadline. */
export async function readBoundedRequestBody(
  request: Request,
  maxBytes: number,
  options: { requireContentLength?: boolean; minBytes?: number } = {},
): Promise<string> {
  const contentLengthHeader = request.headers.get("content-length");
  if (options.requireContentLength && !contentLengthHeader) throw createParseError("Missing Content-Length.");
  if (contentLengthHeader !== null && !/^\d+$/.test(contentLengthHeader)) throw createParseError("Invalid Content-Length header.");

  const declaredLength = contentLengthHeader === null ? undefined : Number.parseInt(contentLengthHeader, 10);
  const minBytes = options.minBytes ?? 0;
  if (declaredLength !== undefined && (!Number.isSafeInteger(declaredLength) || declaredLength < minBytes || declaredLength > maxBytes)) {
    throw createParseError(`Payload must be between ${toText(minBytes)} and ${toText(maxBytes)} bytes.`);
  }
  if (!request.body) {
    if (minBytes === 0 && (declaredLength === undefined || declaredLength === 0)) return "";
    throw createParseError("Payload is required.");
  }
  if (request.signal?.aborted) throw createParseError("Request was cancelled.");

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let consumed = 0;
  let timeoutId: ReturnType<typeof setTimeout> | undefined;
  let abortListener: (() => void) | undefined;
  const cancelReader = async (): Promise<void> => {
    try { await reader.cancel(); } catch { /* stream already closed */ }
  };
  const cancelled = createParseError("Request was cancelled.");
  const timedOut = createParseError("Request body read timed out.");
  const abortPromise = new Promise<"aborted">((resolve) => {
    abortListener = () => resolve("aborted");
    request.signal?.addEventListener("abort", abortListener, { once: true });
  });
  const deadlinePromise = new Promise<"timeout">((resolve) => {
    timeoutId = setTimeout(() => resolve("timeout"), REQUEST_BODY_DEADLINE_MS);
  });

  try {
    while (true) {
      const result = await Promise.race([
        reader.read().then((value) => ({ kind: "read" as const, value })),
        abortPromise.then((kind) => ({ kind })),
        deadlinePromise.then((kind) => ({ kind })),
      ]);
      if (result.kind === "aborted") { await cancelReader(); throw cancelled; }
      if (result.kind === "timeout") { await cancelReader(); throw timedOut; }
      if (result.value.done) break;
      const value = result.value.value;
      if (!value) throw createParseError("Payload read failed.");
      consumed += value.byteLength;
      if (consumed > maxBytes || (declaredLength !== undefined && consumed > declaredLength)) {
        await cancelReader();
        throw createParseError(declaredLength !== undefined && consumed > declaredLength
          ? "Payload length did not match Content-Length." : "Payload too large.");
      }
      chunks.push(value);
    }
    if (consumed < minBytes || (declaredLength !== undefined && consumed !== declaredLength)) {
      await cancelReader();
      throw createParseError("Payload length did not match Content-Length.");
    }
  } catch (error) {
    await cancelReader();
    if (error instanceof Error) throw error;
    throw createParseError("Payload read failed.");
  } finally {
    if (timeoutId) clearTimeout(timeoutId);
    if (abortListener) request.signal?.removeEventListener("abort", abortListener);
  }

  const body = new Uint8Array(consumed);
  let offset = 0;
  for (const chunk of chunks) { body.set(chunk, offset); offset += chunk.byteLength; }
  return new TextDecoder().decode(body);
}

async function readBoundedUrlEncodedBody(request: Request): Promise<string> {
  return readBoundedRequestBody(request, MAX_ONBOARDING_FORM_BYTES, { requireContentLength: true, minBytes: 1 });
}

export async function parseOnboardingMutationForm(request: Request): Promise<ParsedOnboardingForm> {
  if (request.signal?.aborted) {
    throw createParseError("Request was cancelled.");
  }

  const method = request.method.toUpperCase();
  if (method !== "POST") {
    throw createParseError("Unsupported method.");
  }

  const contentType = request.headers.get("content-type");
  if (!contentType || contentType.split(";")[0].trim().toLowerCase() !== "application/x-www-form-urlencoded") {
    throw createParseError("Unsupported content type.");
  }

  const rawBody = await readBoundedUrlEncodedBody(request);
  const params = new URLSearchParams(rawBody);
  let fieldCount = 0;

  for (const [name, value] of params) {
    fieldCount += 1;
    if (Buffer.byteLength(name) > MAX_ONBOARDING_FIELD_NAME_BYTES) {
      throw createParseError("A field name is too long.");
    }

    if (Buffer.byteLength(value) > MAX_ONBOARDING_FIELD_VALUE_BYTES) {
      throw createParseError("A field value is too large.");
    }
  }

  if (fieldCount > MAX_ONBOARDING_FORM_FIELD_COUNT) {
    throw createParseError(`Maximum ${toText(MAX_ONBOARDING_FORM_FIELD_COUNT)} form fields allowed.`);
  }

  return params;
}

export function constantTimeCompare(a: string, b: string): boolean {
  const lhs = Buffer.from(a, "utf8");
  const rhs = Buffer.from(b, "utf8");

  if (lhs.length !== rhs.length) {
    return false;
  }

  return timingSafeEqual(lhs, rhs);
}

export const DEFAULT_NO_CACHE_HEADERS: HeadersInit = {
  "Cache-Control": "no-store, no-cache, must-revalidate, max-age=0",
  Pragma: "no-cache",
  Expires: "0",
};

let mutationQueue: Promise<unknown> = Promise.resolve();

type CsrfRequestInit = RequestInit & {
  /** A retry after token refresh is safe only for an explicitly idempotent operation. */
  csrfIdempotent?: boolean;
  idempotent?: boolean;
};

type CsrfOptions = { idempotent?: boolean };

const currentToken = (): string => {
  if (typeof document === "undefined") return "";
  return document.querySelector('meta[name="csrf-token"]')?.getAttribute("content") || "";
};

const updateToken = (response: { headers: Headers }): void => {
  if (typeof document === "undefined") return;
  const token = response.headers.get("x-csrf-token");
  if (token) {
    document.querySelector('meta[name="csrf-token"]')?.setAttribute("content", token);
  }
};

const updateTokenFromJson = async (response: Response): Promise<string | null> => {
  if (!response.ok) return null;
  const contentType = response.headers.get("content-type");
  if (!contentType || !contentType.toLowerCase().startsWith("application/json")) return null;

  try {
    const body = (await response.clone().json()) as { token?: unknown };
    return typeof body?.token === "string" && body.token.length > 0 ? body.token : null;
  } catch {
    return null;
  }
};

const replayableBody = (body: BodyInit | null | undefined): boolean => {
  if (body === null || body === undefined || typeof body === "string") return true;
  if (body instanceof URLSearchParams || body instanceof FormData || body instanceof Blob) return true;
  if (body instanceof ArrayBuffer || ArrayBuffer.isView(body)) return true;
  return false; // ReadableStream and unknown custom bodies cannot be replayed safely.
};

const mutationHeaders = (init: RequestInit): Headers => {
  const headers = new Headers(init.headers);
  const token = currentToken();
  if (token) headers.set("X-CSRF-Token", token);
  else headers.delete("X-CSRF-Token");
  return headers;
};

async function refreshToken(): Promise<boolean> {
  const response = await fetch("/api/csrf-token", {
    method: "GET",
    credentials: "same-origin",
    cache: "no-store",
  });
  updateToken(response);
  if (response.ok) {
    const headerToken = response.headers.get("x-csrf-token");
    if (headerToken) return true;
    const bodyToken = await updateTokenFromJson(response);
    if (bodyToken) {
      if (typeof document !== "undefined") {
        document.querySelector('meta[name="csrf-token"]')?.setAttribute("content", bodyToken);
      }
      return true;
    }
  }
  return false;
}

export function csrfFetch(
  input: RequestInfo | URL,
  init: CsrfRequestInit = {},
  options: CsrfOptions = {},
): Promise<Response> {
  const method = (init.method || (input instanceof Request ? input.method : "GET")).toUpperCase();
  if (["GET", "HEAD", "OPTIONS"].includes(method)) return fetch(input, init);

  const idempotent = options.idempotent === true || init.csrfIdempotent === true || init.idempotent === true;
  const bodyCanReplay = replayableBody(init.body);
  const { csrfIdempotent: _csrfIdempotent, idempotent: _idempotent, ...requestInit } = init;
  const operation = mutationQueue.then(async () => {
    let response = await fetch(input, { ...requestInit, headers: mutationHeaders(requestInit) });
    updateToken(response);

    if (response.status === 403 && !idempotent) {
      await refreshToken();
      return response;
    }

    // A idempotent operation may safely be retried once after a 403.
    if (response.status === 403 && idempotent && bodyCanReplay) {
      if (await refreshToken()) {
        response = await fetch(input, { ...requestInit, headers: mutationHeaders(requestInit) });
        updateToken(response);
      }
    }
    return response;
  });
  mutationQueue = operation.catch(() => undefined);
  return operation;
}

/**
 * XHR transport for NZB uploads. It shares the mutation lock with csrfFetch,
 * retaining upload progress while preventing concurrent one-time token use.
 */
export function csrfUpload(
  url: string,
  body: XMLHttpRequestBodyInit,
  onProgress?: (loaded: number, total: number) => void,
): Promise<XMLHttpRequest> {
  const operation = mutationQueue.then(() => new Promise<XMLHttpRequest>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.responseType = "json";
    xhr.timeout = 120000;
    xhr.upload.addEventListener("progress", event => {
      if (event.lengthComputable) onProgress?.(event.loaded, event.total);
    });
    xhr.addEventListener("load", () => {
      updateToken({ headers: new Headers({ "X-CSRF-Token": xhr.getResponseHeader("X-CSRF-Token") || "" }) });
      if (xhr.status >= 200 && xhr.status < 300) resolve(xhr);
      else reject(new Error("Upload failed."));
    });
    xhr.addEventListener("error", () => reject(new Error("Upload failed.")));
    xhr.addEventListener("timeout", () => reject(new Error("Upload timed out.")));
    xhr.addEventListener("abort", () => reject(new Error("Upload aborted.")));
    xhr.open("POST", url);
    const token = currentToken();
    if (token) xhr.setRequestHeader("X-CSRF-Token", token);
    xhr.send(body);
  }));
  mutationQueue = operation.catch(() => undefined);
  return operation;
}

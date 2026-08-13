import { describe, expect, it, vi } from "vitest";
import {
  MAX_ONBOARDING_FORM_BYTES,
  parseOnboardingMutationForm,
} from "./onboarding-request.server";

const createFormRequest = (
  body: string,
  contentLength?: number,
  signal?: AbortSignal,
): Request => {
  const encodedLength = Buffer.byteLength(body);
  const requestLength = contentLength ?? encodedLength;

  return new Request("http://localhost/onboarding", {
    method: "POST",
    headers: {
      "content-type": "application/x-www-form-urlencoded",
      "content-length": String(requestLength),
    },
    body,
    signal,
  });
};

describe("onboarding request parser", () => {
  it("parses valid payloads when content-length matches", async () => {
    const body = "provider-host=example.com&provider-pass=p%40ss%25word";

    const request = createFormRequest(body);
    const params = await parseOnboardingMutationForm(request);

    expect(params.get("provider-host")).toBe("example.com");
    expect(params.get("provider-pass")).toBe("p@ss%word");
  });

  it("rejects missing content-length", async () => {
    const body = "a=1";
    const request = new Request("http://localhost/onboarding", {
      method: "POST",
      headers: {
        "content-type": "application/x-www-form-urlencoded",
      },
      body,
    });

    await expect(parseOnboardingMutationForm(request)).rejects.toThrow("Missing Content-Length.");
  });

  it("rejects non-numeric content-length", async () => {
    const request = new Request("http://localhost/onboarding", {
      method: "POST",
      headers: {
        "content-type": "application/x-www-form-urlencoded",
        "content-length": "12ab",
      },
      body: "provider-user=1",
    });

    await expect(parseOnboardingMutationForm(request)).rejects.toThrow("Invalid Content-Length header.");
  });

  it("rejects chunked requests and payload size mismatch", async () => {
    const body = "a=1";
    const request = createFormRequest(body, MAX_ONBOARDING_FORM_BYTES + 1);

    await expect(parseOnboardingMutationForm(request)).rejects.toThrow("Payload must be between 1 and");
  });

  it("rejects declared length mismatch (too short or too long)", async () => {
    const body = "username=alice";
    await expect(parseOnboardingMutationForm(createFormRequest(body, body.length + 2))).rejects.toThrow(
      "Payload length did not match Content-Length.",
    );

    const shortDeclared = createFormRequest("user=alice", 2);
    await expect(parseOnboardingMutationForm(shortDeclared)).rejects.toThrow("Payload length did not match Content-Length.");
  });

  it("rejects when declared length is exceeded", async () => {
    const body = "provider-pass=secret";
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        const bytes = new TextEncoder().encode(body);
        controller.enqueue(bytes);
        controller.close();
      },
    });

    const request = new Request("http://localhost/onboarding", {
      method: "POST",
      headers: {
        "content-type": "application/x-www-form-urlencoded",
        "content-length": "1",
      },
      body: stream,
      duplex: "half",
    } as RequestInit & { duplex: "half" });

    await expect(parseOnboardingMutationForm(request)).rejects.toThrow("Payload length did not match Content-Length.");
  });

  it("preserves multibyte characters by byte count", async () => {
    const params = new URLSearchParams();
    params.set("provider-pass", "pásswörd🚀");
    const body = params.toString();
    const request = createFormRequest(body);

    const parsed = await parseOnboardingMutationForm(request);
    expect(parsed.get("provider-pass")).toBe("pásswörd🚀");
  });

  it("supports caller cancellation while parsing", async () => {
    const aborter = new AbortController();
    const request = createFormRequest("a=1", 3, aborter.signal);
    aborter.abort();

    await expect(parseOnboardingMutationForm(request)).rejects.toThrow("Request was cancelled.");
  });

  it("cancels stalled reads when request aborts", async () => {
    let canceled = false;
    const stream = new ReadableStream<Uint8Array>({
      start() {},
      cancel() {
        canceled = true;
      },
    });

    const aborter = new AbortController();
    const request = new Request("http://localhost/onboarding", {
      method: "POST",
      headers: {
        "content-type": "application/x-www-form-urlencoded",
        "content-length": String(Buffer.byteLength("provider-user=abc")),
      },
      body: stream,
      signal: aborter.signal,
      duplex: "half",
    } as RequestInit & { duplex: "half" });

    const parsePromise = parseOnboardingMutationForm(request);
    setTimeout(() => aborter.abort(), 10);

    await expect(parsePromise).rejects.toThrow("Request was cancelled.");
    expect(canceled).toBe(true);
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { backendClient, normalizeAdminSettingsResponse, normalizeUsenetSettingsResponse } from "./backend-client.server";

const createStreamResponse = (body: string, status = 200, headers: Record<string, string> = {}) => {
  return new Response(body, {
    status,
    headers: {
      "content-type": "application/json",
      ...headers,
    },
  });
};

describe("backend client setup transport", () => {
  let fetchSpy: any;

  beforeEach(() => {
    fetchSpy = vi.spyOn(globalThis, "fetch");
  });

  afterEach(() => {
    fetchSpy.mockRestore();
    vi.useRealTimers();
  });

  it("sends the handoff, renew, and recovery contract as exact URL-encoded bytes", async () => {
    fetchSpy.mockImplementation((url: string) =>
      Promise.resolve(String(url).includes("recovery")
        ? createStreamResponse('{"status":true,"revocationPending":false}')
        : createStreamResponse('{"grant":"grant","expiresAtUtc":"2030-01-01T00:00:00Z"}')),
    );

    await backendClient.issueSetupGrant("Admin+", "p&ss=secret");
    await backendClient.renewSetupGrant("Admin+", "p&ss=secret");
    fetchSpy.mockImplementation(() => Promise.resolve(createStreamResponse('{"status":true,"revocationPending":false}')));
    await backendClient.recoverSetupGrant("Admin+", "p&ss=secret");

    expect(fetchSpy).toHaveBeenCalledTimes(3);
    for (const [, options] of fetchSpy.mock.calls as [string, RequestInit][]) {
      expect(options.body).toBeInstanceOf(URLSearchParams);
      expect(new Headers(options.headers).get("content-type")).toBe(
        "application/x-www-form-urlencoded; charset=UTF-8",
      );
      expect((options.body as URLSearchParams).toString()).toBe("username=Admin%2B&password=p%26ss%3Dsecret");
    }
  });

  it("classifies a setup conflict as retryable and preserves bounded Retry-After", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse('{"status":false,"code":"setup-run-busy"}', 409, { "retry-after": "2" }));

    await expect(backendClient.configureAndRunSetup("grant", { providers: [], indexers: [] })).rejects.toMatchObject({
      code: "conflict",
      status: 409,
      retryAfter: "2",
    });
  });

  it("drops provider secrets during usenet response normalization", () => {
    const normalized = normalizeUsenetSettingsResponse({
      revision: "rev",
      providers: [{
        id: "provider-1",
        host: "news.example.com",
        port: 119,
        ssl: false,
        user: "agent",
        max: 8,
        type: 1,
        hasPassword: true,
        password: "usenet-password-secret",
      }],
    });

    expect(normalized.providers).toHaveLength(1);
    expect(normalized.providers[0]).toMatchObject({
      id: "provider-1",
      host: "news.example.com",
      hasPassword: true,
    });
    expect((normalized.providers[0] as unknown as { password: string }).password).toBeUndefined();
    expect(JSON.stringify(normalized)).not.toContain("usenet-password-secret");
  });

  it("redacts admin settings write-only values and strips ARR API keys", () => {
    const raw = {
      config: {
        "api.key": "admin-secret",
        "webdav.pass": "dav-secret",
        "cache.l2.access-key": "l2-access-secret",
        "cache.l2.secret-key": "l2-secret-key",
        "arr.instances": '{"RadarrInstances":[{"Host":"http://radarr:7878","ApiKey":"arr-secret","HasApiKey":true}],"SonarrInstances":[],"QueueRules":[]}',
      },
      hasSecrets: {
        "api.key": true,
        "webdav.pass": true,
        "cache.l2.access-key": true,
        "cache.l2.secret-key": true,
        "arr.instances": true,
      },
      extra: "should-be-dropped",
    };

    const normalized = normalizeAdminSettingsResponse(raw);
    expect(normalized.config["api.key"]).toBe("");
    expect(normalized.config["webdav.pass"]).toBe("");
    expect(normalized.config["cache.l2.access-key"]).toBe("");
    expect(normalized.config["cache.l2.secret-key"]).toBe("");
    expect(JSON.stringify(normalized.config)).not.toContain("arr-secret");

    const arr = JSON.parse(normalized.config["arr.instances"]);
    expect(arr.RadarrInstances).toEqual([{ Host: "http://radarr:7878", ApiKey: "", HasApiKey: true }]);
    expect(arr.SonarrInstances).toEqual([]);
    expect(arr.QueueRules).toEqual([]);
    expect(normalized.hasSecrets["arr.instances"]).toBe(true);
    expect((normalized as { extra?: string }).extra).toBeUndefined();
  });

  it("aborts configure requests when caller signal is already aborted", async () => {
    const aborter = new AbortController();
    aborter.abort();

    await expect(
      backendClient.configureAndRunSetup(
        "grant",
        {
          providers: [],
          indexers: [],
        },
        {
          signal: aborter.signal,
        },
      ),
    ).rejects.toThrow("Setup request was cancelled.");

    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("aborts read of stalled backend responses", async () => {
    let canceled = false;
    const stream = new ReadableStream<Uint8Array>({
      start() {},
      cancel() {
        canceled = true;
      },
    });

    fetchSpy.mockResolvedValue(new Response(stream, {
      status: 200,
      headers: {
        "content-type": "application/json",
      },
    }));

    const aborter = new AbortController();
    const promise = backendClient.configureAndRunSetup("grant", { providers: [], indexers: [] }, { signal: aborter.signal });

    setTimeout(() => aborter.abort(), 10);

    await expect(promise).rejects.toThrow("Setup request was cancelled.");
    expect(canceled).toBe(true);
  });

  it("enforces request timeout on setup requests", async () => {
    vi.useFakeTimers();

    fetchSpy.mockImplementation((_: RequestInfo, options?: RequestInit) => {
      return new Promise<Response>((_, reject) => {
        options?.signal?.addEventListener(
          "abort",
          () => {
            reject(new DOMException("The operation was aborted.", "AbortError"));
          },
          { once: true },
        );
      });
    });

    const pending = backendClient.configureAndRunSetup("grant", { providers: [], indexers: [] });
    const assertion = expect(pending).rejects.toThrow("Setup request timed out.");

    // Setup uses its own whole-operation budget; the ordinary 12s request
    // budget must not abort a finite setup operation.
    await vi.advanceTimersByTimeAsync(12_050);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(588_000);

    await assertion;
  });

  it("preserves exact backend step names and only allowlists safe status fields", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse(JSON.stringify({
      enabled: true,
      completed: false,
      revocationPending: true,
      steps: [
        { name: "provider-indexers", state: "warning", reason: "provider-failed", code: "provider-failed", serviceReady: false },
        { name: "arr-key-discovery", state: "complete", reason: "secret-body", code: "secret-body", serviceReady: true },
        { name: "not safe body message", state: "failed", reason: "password", code: "password" },
      ],
      services: [
        { name: "jellyfin", ready: true },
        { name: "sonarr", ready: false, reason: "backend-unavailable" },
        { name: "radarr", ready: true },
        { name: "prowlarr", ready: true },
        { name: "provider", ready: false, reason: "provider-failed" },
      ],
    })));

    const status = await backendClient.getSetupStatus();
    expect(status.revocationPending).toBe(true);
    expect(status.steps.map((step) => step.name)).toEqual(["provider-indexers", "arr-key-discovery", "setup"]);
    expect(status.steps[0]?.code).toBe("provider-failed");
    expect(status.steps[2]?.reason).toBe("unknown");
    expect(status.services.map((service) => service.name)).toEqual(["jellyfin", "sonarr", "radarr", "prowlarr", "provider"]);
    expect(JSON.stringify(status)).not.toContain("password");
    expect(JSON.stringify(status)).not.toContain("secret-body");
  });

  it("calls completed-service verification with the internal transport and no setup grant", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse(JSON.stringify({
      enabled: true,
      completed: true,
      repairRequired: false,
      steps: [],
      services: [],
    })));

    await backendClient.verifySetupStatus();

    const [url, options] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/setup/status/verify");
    expect(new Headers(options.headers).has("x-setup-grant")).toBe(false);
    expect(new Headers(options.headers).get("x-api-key")).toBe(process.env.FRONTEND_BACKEND_API_KEY || "");
    expect(options.body).toBe("");
  });

  it("calls the recovery contract with credentials and no setup grant", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse('{"status":true,"revocationPending":false}'));

    const result = await backendClient.recoverSetupGrant("alice", "secret", {});

    expect(result.revocationPending).toBe(false);
    const [url, options] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/setup/grant/recovery");
    expect(new Headers(options.headers).has("x-setup-grant")).toBe(false);
  });

  it("rejects malformed response content-length", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse("{}", 200, { "content-length": "abc" }));

    await expect(backendClient.configureAndRunSetup("grant", { providers: [], indexers: [] })).rejects.toThrow(
      "Backend response had invalid content-length header.",
    );
  });

  it("rejects response body length mismatch", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse("{}", 200, { "content-length": "100" }));

    await expect(backendClient.configureAndRunSetup("grant", { providers: [], indexers: [] })).rejects.toThrow(
      "Backend response body length mismatch.",
    );
  });

  it("rejects non-object admin settings responses", () => {
    expect(() => normalizeAdminSettingsResponse(null)).toThrow("Invalid admin settings response.");
  });

  it("rejects oversized setup responses before JSON parsing", async () => {
    const tooLarge = "x".repeat(24 * 1024 + 1);
    fetchSpy.mockResolvedValue(
      new Response(tooLarge, {
        status: 200,
        headers: {
          "content-length": String(tooLarge.length),
          "content-type": "application/json",
        },
      }),
    );

    await expect(
      backendClient.configureAndRunSetup("grant", {
        providers: [],
        indexers: [],
      }),
    ).rejects.toThrow("Backend response was too large.");
  });

  it("rejects status:false responses with allowlisted messaging", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse('{"status":false,"error":"INTERNAL"}', 200));

    await expect(
      backendClient.issueSetupGrant("alice", "pass"),
    ).rejects.toThrow("setup-handoff: request failed.");
  });

  it("maps allowlisted status error codes", async () => {
    fetchSpy.mockResolvedValue(createStreamResponse('{"status":false,"code":"bad-request"}', 200));

    await expect(
      backendClient.issueSetupGrant("alice", "pass"),
    ).rejects.toThrow("setup-handoff: invalid request.");
  });

  it("cancels response body on non-OK responses", async () => {
    const cancelSpy = vi.fn();
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode("{}"));
        controller.close();
      },
      cancel() {
        cancelSpy();
      },
    });

    fetchSpy.mockResolvedValue(new Response(stream, { status: 500 }));

    await expect(
      backendClient.configureAndRunSetup("grant", {
        providers: [],
        indexers: [],
      }),
    ).rejects.toThrow("setup-configure: backend unavailable.");

    expect(cancelSpy).toHaveBeenCalledTimes(1);
  });

  it("unwraps nested queue payloads for contract compatibility", async () => {
    const nested = {
      queue: {
        slots: [{ nzo_id: "1", priority: "0", filename: "a", cat: "x", percentage: "100", true_percentage: "100", status: "done", mb: "1", mbleft: "0" }],
        noofslots: 1,
      },
    };

    fetchSpy.mockResolvedValue(createStreamResponse(JSON.stringify(nested), 200));

    const queue = await backendClient.getQueue(10);
    expect(queue.slots).toEqual(nested.queue.slots);
    expect(queue.noofslots).toBe(1);
  });

  it("unwraps nested history payloads for contract compatibility", async () => {
    const nested = {
      history: {
        slots: [
          {
            nzo_id: "2",
            nzb_name: "file",
            name: "file",
            category: "cat",
            status: "done",
            bytes: 10,
            storage: "ssd",
            download_time: 1,
            fail_message: "",
          },
        ],
        noofslots: 1,
      },
    };

    fetchSpy.mockResolvedValue(createStreamResponse(JSON.stringify(nested), 200));

    const history = await backendClient.getHistory(10);
    expect(history.slots).toEqual(nested.history.slots);
    expect(history.noofslots).toBe(1);
  });

  it("sends legacy queue payload with ONLY limit", async () => {
    const top = {
      queue: {
        slots: [],
        noofslots: 0,
      },
    };

    fetchSpy.mockImplementation((url: string) => {
      const target = String(url);
      expect(target).toContain("mode=queue");
      expect(target).toContain("limit=");
      expect(target).not.toContain("pageSize=");
      return Promise.resolve(createStreamResponse(JSON.stringify(top), 200));
    });

    await backendClient.getQueue(5);
  });

  it("sends legacy history payload with ONLY pageSize", async () => {
    const top = {
      history: {
        slots: [],
        noofslots: 0,
      },
    };

    fetchSpy.mockImplementation((url: string) => {
      const target = String(url);
      expect(target).toContain("mode=history");
      expect(target).toContain("pageSize=");
      expect(target).not.toContain("?mode=history&limit=");
      return Promise.resolve(createStreamResponse(JSON.stringify(top), 200));
    });

    await backendClient.getHistory(3);
  });

  it("cancels redirecting backend responses and never follows them", async () => {
    fetchSpy.mockImplementation((url: string, options?: RequestInit) => {
      expect(options?.redirect).toBe("manual");
      return Promise.resolve(
        new Response("", {
          status: 302,
          headers: {
            location: "https://evil.example/",
          },
        }),
      );
    });

    await expect(backendClient.issueSetupGrant("alice", "pass")).rejects.toThrow("setup-handoff: request failed.");
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect((fetchSpy.mock.calls[0]?.[0] as string)).not.toContain("evil.example");
  });

  it("caps queue/history JSON payloads to the history-specific limit", async () => {
    const tooLarge = "x".repeat(4 * 1024 * 1024 + 1);
    fetchSpy.mockResolvedValue(
      new Response(tooLarge, {
        status: 200,
        headers: {
          "content-length": String(tooLarge.length),
          "content-type": "application/json",
        },
      }),
    );

    await expect(backendClient.getQueue(1)).rejects.toThrow("Backend response was too large.");
  });
});

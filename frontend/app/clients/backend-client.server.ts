type SetupRequestOptions = {
    signal?: AbortSignal;
    /** Opaque browser source bound by the authenticated frontend proxy. */
    frontendClientSource?: string;
};

// Keep this wire contract identical to SetupGrantRequest on the backend. In
// particular, URLSearchParams produces deterministic bytes and never emits a
// multipart boundary or a credential-bearing FormData filename.
export const SETUP_FORM_CONTENT_TYPE = "application/x-www-form-urlencoded; charset=UTF-8";

const createSetupCredentialsBody = (username: string, password: string): URLSearchParams =>
    new URLSearchParams([ ["username", username], ["password", password] ]);

export type BackendRequestErrorCode =
    | "timeout"
    | "canceled"
    | "bad-request"
    | "access-denied"
    | "not-found"
    | "conflict"
    | "rate-limited"
    | "backend-unavailable"
    | "request-failed"
    | "invalid-response"
    | "response-too-large"
    | "response-length-mismatch"
    | "invalid-content-length"
    | "response-too-complex";

export class BackendRequestError extends Error {
    public readonly code: BackendRequestErrorCode;
    public readonly status?: number;
    public readonly retryAfter?: string;

    public constructor(code: BackendRequestErrorCode, message: string, status?: number, retryAfter?: string) {
        super(message);
        this.name = "BackendRequestError";
        this.code = code;
        this.status = status;
        this.retryAfter = retryAfter;
    }
}

const REQUEST_TIMEOUT = new BackendRequestError("timeout", "Setup request timed out.");
const REQUEST_CANCELED = new BackendRequestError("canceled", "Setup request was cancelled.");

type RequestLifetime = {
    timeoutId?: ReturnType<typeof setTimeout>;
    cleanup: () => void;
    readonly isTimedOut: () => boolean;
    readonly isCanceledByCaller: () => boolean;
    signal: AbortSignal;
};

class BackendClient {
    private readonly _apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
    private readonly _requestTimeoutMs = Number(process.env.FRONTEND_BACKEND_REQUEST_TIMEOUT_MS || "12000");
    // Setup is one finite backend operation containing bounded validation and
    // several sequential service calls. Keep this budget separate from
    // ordinary browser/API requests.
    private readonly _setupRequestTimeoutMs = Number(process.env.FRONTEND_SETUP_REQUEST_TIMEOUT_MS || "600000");
    private readonly _setupResponseBytes = 24 * 1024;
    private readonly _queueHistoryResponseBytes = 4 * 1024 * 1024;
    private readonly _defaultResponseBytes = 1024 * 1024;
    private readonly _jsonMaxDepth = 64;

    private readonly _setupStatusPath = "/api/setup/status";
    private readonly _setupStatusVerifyPath = "/api/setup/status/verify";
    private readonly _setupHandoffPath = "/api/setup/handoff";
    private readonly _setupRepairGrantPath = "/api/setup/grant/repair";
    private readonly _setupRenewPath = "/api/setup/grant/renew";
    private readonly _setupRevokePath = "/api/setup/grant/revoke";
    private readonly _setupRecoveryPath = "/api/setup/grant/recovery";
    private readonly _setupRetryPath = "/api/setup/retry";
    private readonly _setupConfigurePath = "/api/setup/configuration";

    public async isOnboarding(): Promise<boolean> {
        const response = await this.fetchJson<BackendApiResponse>(this.baseUrl("/api/is-onboarding"), {
            method: "GET",
            context: "onboarding-check",
            apiKey: this._apiKey,
        });

        return Boolean(response?.isOnboarding);
    }

    public async createAccount(username: string, password: string): Promise<boolean> {
        const formData = new FormData();
        formData.append("username", username);
        formData.append("password", password);
        formData.append("type", "admin");

        const response = await this.fetchJson<BackendApiResponse>(this.baseUrl("/api/create-account"), {
            method: "POST",
            body: formData,
            context: "create-account",
        });

        return Boolean(response?.status);
    }

    public async authenticate(username: string, password: string): Promise<boolean> {
        const formData = new FormData();
        formData.append("username", username);
        formData.append("password", password);
        formData.append("type", "admin");

        const response = await this.fetchJson<BackendApiResponse>(this.baseUrl("/api/authenticate"), {
            method: "POST",
            body: formData,
            context: "authenticate",
        });

        return Boolean(response?.authenticated);
    }

    public async getSetupStatus(): Promise<SetupStatus> {
        const status = await this.fetchJson<RawSetupStatus>(this.baseUrl(this._setupStatusPath), {
            method: "GET",
            context: "setup-status",
            maxResponseBytes: this._setupResponseBytes,
        });

        return this.normalizeSetupStatus(status);
    }

    /** Verify completed managed services using only the internal frontend transport; no setup grant is needed. */
    public async verifySetupStatus(options?: SetupRequestOptions): Promise<SetupStatus> {
        const response = await this.fetchJson<RawSetupStatus>(this.baseUrl(this._setupStatusVerifyPath), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: "",
            context: "setup-status-verify",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
        });
        return this.normalizeSetupStatus(response);
    }

    public async issueSetupGrant(username: string, password: string, options?: SetupRequestOptions): Promise<SetupGrantResponse> {
        const body = createSetupCredentialsBody(username, password);

        const response = await this.fetchJson<RawSetupGrantResponse>(this.baseUrl(this._setupHandoffPath), {
            method: "POST",
            headers: { "Content-Type": SETUP_FORM_CONTENT_TYPE },
            body,
            context: "setup-handoff",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
            frontendClientSource: options?.frontendClientSource,
        });

        return this.normalizeSetupGrantResponse(response);
    }

    public async issueRepairGrant(username: string, password: string, options?: SetupRequestOptions): Promise<SetupGrantResponse> {
        const body = createSetupCredentialsBody(username, password);
        const response = await this.fetchJson<RawSetupGrantResponse>(this.baseUrl(this._setupRepairGrantPath), {
            method: "POST",
            headers: { "Content-Type": SETUP_FORM_CONTENT_TYPE },
            body,
            context: "setup-repair-grant",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
            frontendClientSource: options?.frontendClientSource,
        });
        return this.normalizeSetupGrantResponse(response);
    }

    public async renewSetupGrant(username: string, password: string, options?: SetupRequestOptions): Promise<SetupGrantResponse> {
        const body = createSetupCredentialsBody(username, password);

        const response = await this.fetchJson<RawSetupGrantResponse>(this.baseUrl(this._setupRenewPath), {
            method: "POST",
            headers: { "Content-Type": SETUP_FORM_CONTENT_TYPE },
            body,
            context: "setup-renew",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
            frontendClientSource: options?.frontendClientSource,
        });

        return this.normalizeSetupGrantResponse(response);
    }

    public async revokeSetupGrant(rawGrant: string, options?: SetupRequestOptions): Promise<void> {
        await this.fetchJson<BackendApiResponse>(this.baseUrl(this._setupRevokePath), {
            method: "POST",
            grant: rawGrant,
            headers: { "Content-Type": "application/json" },
            body: "",
            context: "setup-revoke",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
            frontendClientSource: options?.frontendClientSource,
        });
    }

    public async recoverSetupGrant(username: string, password: string, options?: SetupRequestOptions): Promise<SetupRecoveryResponse> {
        const body = createSetupCredentialsBody(username, password);
        const response = await this.fetchJson<RawSetupRecoveryResponse>(this.baseUrl(this._setupRecoveryPath), {
            method: "POST",
            headers: { "Content-Type": SETUP_FORM_CONTENT_TYPE },
            body,
            context: "setup-recovery",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
            frontendClientSource: options?.frontendClientSource,
        });
        return {
            revocationPending: response.revocationPending === true || response.RevocationPending === true,
        };
    }

    public async configureAndRunSetup(
        grant: string,
        request: SetupConfigureRequest,
        options?: SetupRequestOptions,
    ): Promise<SetupStatus> {
        const response = await this.fetchJson<RawSetupStatus>(this.baseUrl(this._setupConfigurePath), {
            method: "POST",
            grant,
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                providers: request.providers.map((provider) => ({
                    type: String(provider.Type),
                    host: provider.Host,
                    port: provider.Port,
                    useSsl: provider.UseSsl,
                    user: provider.User,
                    pass: provider.Pass,
                    maxConnections: provider.MaxConnections,
                })),
                indexers: request.indexers.map((indexer) => ({
                    name: indexer.Name,
                    baseUrl: indexer.Url,
                    apiKey: indexer.ApiKey,
                    allowPrivateNetwork: indexer.AllowPrivateNetwork === true,
                })),
            }),
            context: "setup-configure",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
        });

        return this.normalizeSetupStatus(response);
    }

    public async retrySetup(grant: string, options?: SetupRequestOptions): Promise<SetupStatus> {
        const response = await this.fetchJson<RawSetupStatus>(this.baseUrl(this._setupRetryPath), {
            method: "POST",
            grant,
            headers: { "Content-Type": "application/json" },
            body: "",
            context: "setup-retry",
            signal: options?.signal,
            setupOperation: true,
            maxResponseBytes: this._setupResponseBytes,
        });

        return this.normalizeSetupStatus(response);
    }

    public async getQueue(limit: number): Promise<QueueResponse> {
        const url = this.baseUrl(`/api?mode=queue&limit=${limit}`);
        const response = await this.fetchJson<BackendQueuePayload>(url, {
            method: "GET",
            context: "queue",
            apiKey: this._apiKey,
            maxResponseBytes: this._queueHistoryResponseBytes,
        });

        return this.parseQueueResponse(response);
    }

    public async getHistory(limit: number): Promise<HistoryResponse> {
        const url = this.baseUrl(`/api?mode=history&pageSize=${limit}`);
        const response = await this.fetchJson<BackendQueuePayload>(url, {
            method: "GET",
            context: "history",
            apiKey: this._apiKey,
            maxResponseBytes: this._queueHistoryResponseBytes,
        });

        return this.parseHistoryResponse(response);
    }

    public async addNzb(nzbFile: File): Promise<string> {
        const config = await this.getConfig(["api.manual-category"]);
        const category = config.find((item) => item.configName === "api.manual-category")?.configValue || "uncategorized";

        const formData = new FormData();
        formData.append("nzbFile", nzbFile, nzbFile.name);
        const response = await this.fetchJson<BackendApiResponse>(
            this.baseUrl(`/api?mode=addfile&cat=${category}&priority=0&pp=0`),
            {
                method: "POST",
                body: formData,
                context: "add-nzb",
            },
        );

        if (!Array.isArray((response as { nzo_ids?: unknown[] }).nzo_ids) || (response as { nzo_ids?: unknown[] }).nzo_ids?.length !== 1) {
            throw new BackendRequestError("invalid-response", "Failed to add NZB file: unexpected response format.");
        }

        return `${(response as { nzo_ids?: unknown[] }).nzo_ids?.[0]}`;
    }

    public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
        const formData = new FormData();
        formData.append("directory", directory);
        const response = await this.fetchJson<BackendApiResponse & { items?: unknown }>(
            this.baseUrl("/api/list-webdav-directory"),
            {
                method: "POST",
                body: formData,
                context: "list-webdav-directory",
            },
        );

        return Array.isArray(response.items) ? (response.items as DirectoryItem[]) : [];
    }

    public async getConfig(keys: string[]): Promise<ConfigItem[]> {
        const formData = new FormData();
        for (const key of keys) {
            formData.append("config-keys", key);
        }

        const response = await this.fetchJson<BackendApiResponse & { configItems?: unknown }>(
            this.baseUrl("/api/get-config"),
            {
                method: "POST",
                body: formData,
                context: "get-config",
            },
        );

        return Array.isArray(response.configItems) ? (response.configItems as ConfigItem[]) : [];
    }

    public async getAdminSettings(): Promise<AdminSettingsResponse> {
        const response = await this.fetchJson<unknown>(this.baseUrl("/api/admin-settings"), {
            method: "GET",
            context: "get-admin-settings",
            maxResponseBytes: 512 * 1024,
        });
        return normalizeAdminSettingsResponse(response);
    }

    public async updateAdminSettings(request: AdminSettingsRequest): Promise<AdminSettingsResponse> {
        const response = await this.fetchJson<unknown>(this.baseUrl("/api/admin-settings"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(request),
            context: "update-admin-settings",
            maxResponseBytes: 512 * 1024,
        });
        return normalizeAdminSettingsResponse(response);
    }

    public async getUsenetSettings(): Promise<UsenetSettingsResponse> {
        const response = await this.fetchJson<unknown>(this.baseUrl("/api/admin-settings/usenet"), {
            method: "GET",
            context: "get-usenet-settings",
            maxResponseBytes: 128 * 1024,
        });
        return normalizeUsenetSettingsResponse(response);
    }

    public async updateUsenetSettings(request: UsenetSettingsRequest): Promise<UsenetSettingsResponse> {
        const response = await this.fetchJson<unknown>(this.baseUrl("/api/admin-settings/usenet"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(request),
            context: "update-usenet-settings",
            maxResponseBytes: 128 * 1024,
        });
        return normalizeUsenetSettingsResponse(response);
    }

    public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
        const formData = new FormData();
        for (const item of configItems) {
            formData.append(item.configName, item.configValue);
        }

        const response = await this.fetchJson<BackendApiResponse>(this.baseUrl("/api/update-config"), {
            method: "POST",
            body: formData,
            context: "update-config",
        });

        return Boolean(response?.status);
    }

    public async getEncryptionStatus(): Promise<EncryptionStatus> {
        return this.fetchJson<EncryptionStatus>(this.baseUrl("/api/encryption-status"), {
            method: "GET",
            context: "encryption-status",
            apiKey: this._apiKey,
        });
    }

    public async acknowledgePostMigration(): Promise<void> {
        await this.fetchJson<BackendApiResponse>(this.baseUrl("/api/encryption-status/acknowledge-post-migration"), {
            method: "POST",
            context: "acknowledge-post-migration",
        });
    }

    public async getHealthCheckQueue(pageSize?: number): Promise<HealthCheckQueueResponse> {
        let url = this.baseUrl("/api/get-health-check-queue");
        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        return this.fetchJson<HealthCheckQueueResponse>(url, {
            method: "GET",
            context: "health-check-queue",
            apiKey: this._apiKey,
        });
    }

    public async getHealthCheckHistory(pageSize?: number): Promise<HealthCheckHistoryResponse> {
        let url = this.baseUrl("/api/get-health-check-history");
        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        return this.fetchJson<HealthCheckHistoryResponse>(url, {
            method: "GET",
            context: "health-check-history",
            apiKey: this._apiKey,
        });
    }

    private async fetchJson<T>(url: string, options: {
        method: "GET" | "POST";
        body?: BodyInit;
        grant?: string;
        headers?: HeadersInit;
        apiKey?: string;
        context: string;
        signal?: AbortSignal;
        maxResponseBytes?: number;
        setupOperation?: boolean;
        frontendClientSource?: string;
    }): Promise<T> {
        const responseLifetime = this.beginRequestLifetime(options.signal, options.setupOperation);
        let response: Response;

        try {
            response = await this.fetchWithTimeout(url, options, responseLifetime);

            if (!response.ok) {
                await this.cancelResponse(response);
                throw this.createHttpError(options.context, response.status, response.headers.get("retry-after") || undefined);
            }

            const responseText = await this.readResponseText(response, responseLifetime, options.maxResponseBytes);
            const parsed = this.parseJsonResponse(responseText, options.context);

            if (typeof (parsed as BackendApiResponse)?.status === "boolean" && (parsed as BackendApiResponse).status === false) {
                const safeMessage = this.formatBackendError(parsed as BackendApiResponse);
                const actionCode = this.getBackendActionCode(parsed as BackendApiResponse);
                throw new BackendRequestError(
                    actionCode || "request-failed",
                    safeMessage
                        ? `${options.context}: ${safeMessage}`
                        : this.getSafeErrorMessage(options.context, response.status),
                    response.status,
                );
            }

            return parsed as T;
        } catch (error) {
            // Undici/Node may reject with the abort reason (or with its own
            // DOMException).  Never expose that reason and always classify it
            // from our request lifetime instead.
            if (responseLifetime.signal.aborted || (error instanceof DOMException && error.name === "AbortError")) {
                throw this.translateAbortError(responseLifetime);
            }

            throw error;
        } finally {
            responseLifetime.cleanup();
        }
    }

    private beginRequestLifetime(requestSignal?: AbortSignal, setupOperation = false): RequestLifetime {
        const configuredTimeout = setupOperation ? this._setupRequestTimeoutMs : this._requestTimeoutMs;
        const fallbackTimeout = setupOperation ? 600_000 : 12_000;
        const timeoutMs = Number.isSafeInteger(configuredTimeout) && configuredTimeout > 0 ? configuredTimeout : fallbackTimeout;
        const timeoutController = new AbortController();
        let timedOut = false;
        let canceledByCaller = Boolean(requestSignal?.aborted);

        const onRequestAbort = () => {
            canceledByCaller = true;
            timeoutController.abort(REQUEST_CANCELED);
        };

        requestSignal?.addEventListener("abort", onRequestAbort, { once: true });
        const timeoutId = setTimeout(() => {
            timedOut = true;
            timeoutController.abort(REQUEST_TIMEOUT);
        }, timeoutMs);

        return {
            timeoutId,
            isTimedOut: () => timedOut,
            isCanceledByCaller: () => canceledByCaller,
            signal: timeoutController.signal,
            cleanup: () => {
                clearTimeout(timeoutId);
                requestSignal?.removeEventListener("abort", onRequestAbort);
            },
        };
    }

    private translateAbortError(lifetime: RequestLifetime): BackendRequestError {
        return lifetime.isTimedOut() ? REQUEST_TIMEOUT : REQUEST_CANCELED;
    }

    private async fetchWithTimeout(url: string, options: {
        method: "GET" | "POST";
        body?: BodyInit;
        grant?: string;
        headers?: HeadersInit;
        apiKey?: string;
        context: string;
        signal?: AbortSignal;
        maxResponseBytes?: number;
        setupOperation?: boolean;
        frontendClientSource?: string;
    }, lifetime: RequestLifetime): Promise<Response> {
        if (options.signal?.aborted) {
            throw REQUEST_CANCELED;
        }

        const headers: HeadersInit = {
            ...(options.apiKey === undefined ? { "x-api-key": this._apiKey } : { "x-api-key": options.apiKey }),
            ...(options.grant ? { "x-setup-grant": options.grant } : {}),
            ...(options.frontendClientSource && /^[A-Za-z0-9_.:@-]{1,128}$/.test(options.frontendClientSource)
                ? { "x-frontend-client-source": options.frontendClientSource }
                : {}),
            ...(options.headers || {}),
        };

        return await fetch(url, {
            method: options.method,
            signal: lifetime.signal,
            headers,
            body: options.body,
            redirect: "manual",
        });
    }

    private async cancelResponse(response: Response): Promise<void> {
        try {
            await response.body?.cancel();
        } catch {
            // ignored
        }
    }

    private createHttpError(context: string, status: number, retryAfter?: string): BackendRequestError {
        const code: BackendRequestErrorCode = status === 400
            ? "bad-request"
            : status === 409
                ? "conflict"
            : status === 429
                ? "rate-limited"
                : status === 401 || status === 403
                ? "access-denied"
                : status === 404 || status === 410
                    ? "not-found"
                    : status >= 500
                        ? "backend-unavailable"
                        : "request-failed";
        return new BackendRequestError(code, this.getSafeErrorMessage(context, status), status, retryAfter);
    }

    private async readResponseText(
        response: Response,
        lifetime: RequestLifetime,
        maxResponseBytes = this._defaultResponseBytes,
    ): Promise<string> {
        if (lifetime.signal.aborted) {
            await this.cancelResponse(response);
            throw this.translateAbortError(lifetime);
        }

        const contentLengthHeader = response.headers.get("content-length");
        let declaredLength: number | undefined;

        if (contentLengthHeader !== null) {
            if (!/^\d+$/.test(contentLengthHeader)) {
                await this.cancelResponse(response);
                throw new BackendRequestError("invalid-content-length", "Backend response had invalid content-length header.");
            }

            declaredLength = Number.parseInt(contentLengthHeader, 10);
            if (!Number.isInteger(declaredLength) || declaredLength > maxResponseBytes) {
                await this.cancelResponse(response);
                throw new BackendRequestError("response-too-large", "Backend response was too large.");
            }
        }

        if (!response.body) {
            if (declaredLength !== undefined && declaredLength !== 0) {
                throw new BackendRequestError("response-length-mismatch", "Backend response body length mismatch.");
            }
            return "";
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        const chunks: Uint8Array[] = [];
        let total = 0;
        let onAbort: (() => void) | undefined;
        const cancelReader = async (): Promise<void> => {
            try {
                await reader.cancel();
            } catch {
                // The stream may already have errored or closed.
            }
        };
        const abortPromise = new Promise<"aborted">((resolve) => {
            onAbort = () => resolve("aborted");
            lifetime.signal.addEventListener("abort", onAbort, { once: true });
        });

        try {
            while (true) {
                        type StreamResult =
                    | { kind: "stream"; value: { done: boolean; value?: Uint8Array } }
                    | { kind: "aborted" };

                const readResult = await Promise.race<StreamResult>([
                    reader.read().then((result) => ({ kind: "stream", value: result } as const)),
                    abortPromise.then(() => ({ kind: "aborted" } as const)),
                ]);

                if (readResult.kind === "aborted") {
                    await cancelReader();
                    throw lifetime.isTimedOut() ? REQUEST_TIMEOUT : REQUEST_CANCELED;
                }

                const { done, value } = readResult.value;
                if (done) {
                    break;
                }

                if (!value) {
                    throw new BackendRequestError("invalid-response", "Backend response was invalid.");
                }

                total += value.length;
                if (declaredLength !== undefined && total > declaredLength) {
                    await cancelReader();
                    throw new BackendRequestError("response-length-mismatch", "Backend response body length mismatch.");
                }

                if (total > maxResponseBytes) {
                    await cancelReader();
                    throw new BackendRequestError("response-too-large", "Backend response was too large.");
                }

                chunks.push(value);
            }
        } catch (error) {
            // A locked body cannot be cancelled through response.body; always
            // cancel the reader on every read failure, including parser and
            // transport failures.
            await cancelReader();
            if (error === REQUEST_TIMEOUT || error === REQUEST_CANCELED) {
                throw this.translateAbortError(lifetime);
            }

            throw error;
        } finally {
            if (onAbort) {
                lifetime.signal.removeEventListener("abort", onAbort);
            }
        }

        if (declaredLength !== undefined && total !== declaredLength) {
            await cancelReader();
            throw new BackendRequestError("response-length-mismatch", "Backend response body length mismatch.");
        }

        const bytes = new Uint8Array(total);
        let offset = 0;
        for (const chunk of chunks) {
            bytes.set(chunk, offset);
            offset += chunk.byteLength;
        }

        return decoder.decode(bytes);
    }

    private parseJsonResponse(rawResponse: string, context: string): BackendApiResponse {
        if (rawResponse.length === 0) {
            return {};
        }

        let parsed: unknown;
        try {
            parsed = JSON.parse(rawResponse);
        } catch {
            throw new BackendRequestError("invalid-response", `Invalid ${context} response.`);
        }

        this.validateJsonDepthAndShape(parsed, 0);
        return parsed as BackendApiResponse;
    }

    private parseQueueResponse(payload: BackendQueuePayload): QueueResponse {
        const queueContainer = this.extractQueueContainer(payload);
        return {
            slots: queueContainer.slots,
            noofslots: queueContainer.noofslots,
        };
    }

    private parseHistoryResponse(payload: BackendQueuePayload): HistoryResponse {
        const historyContainer = this.extractHistoryContainer(payload);
        return {
            slots: historyContainer.slots,
            noofslots: historyContainer.noofslots,
        };
    }

    private extractQueueContainer(payload: BackendQueuePayload): { slots: QueueSlot[]; noofslots: number } {
        const nestedQueue = (payload as { queue?: unknown }).queue;
        if (nestedQueue && typeof nestedQueue === "object" && !Array.isArray(nestedQueue)) {
            const container = nestedQueue as { slots?: unknown; noofslots?: unknown };
            const slots = Array.isArray(container.slots) ? container.slots as QueueSlot[] : [];
            const noofslots = Number.isInteger(Number(container.noofslots)) ? Number(container.noofslots) : 0;
            return { slots, noofslots };
        }

        const slots = Array.isArray(payload.queue) ? payload.queue : [];
        return { slots, noofslots: Number.isInteger(Number(payload.noofslots)) ? Number(payload.noofslots) : 0 };
    }

    private extractHistoryContainer(payload: BackendQueuePayload): { slots: HistorySlot[]; noofslots: number } {
        const nestedHistory = (payload as { history?: unknown }).history;
        if (nestedHistory && typeof nestedHistory === "object" && !Array.isArray(nestedHistory)) {
            const container = nestedHistory as { slots?: unknown; noofslots?: unknown };
            const slots = Array.isArray(container.slots) ? container.slots as HistorySlot[] : [];
            const noofslots = Number.isInteger(Number(container.noofslots)) ? Number(container.noofslots) : 0;
            return { slots, noofslots };
        }

        const slots = Array.isArray(payload.history) ? payload.history : [];
        return { slots, noofslots: Number.isInteger(Number(payload.noofslots)) ? Number(payload.noofslots) : 0 };
    }

    private validateJsonDepthAndShape(value: unknown, depth: number): void {
        if (depth > this._jsonMaxDepth) {
            throw new BackendRequestError("response-too-complex", "Backend response was too complex.");
        }

        if (value === null || typeof value !== "object") {
            return;
        }

        if (Array.isArray(value)) {
            if (value.length > 512) {
                throw new BackendRequestError("response-too-large", "Backend response was too large.");
            }

            for (const item of value) {
                this.validateJsonDepthAndShape(item, depth + 1);
            }

            return;
        }

        const objectKeys = Object.keys(value);
        if (objectKeys.length > 128) {
            throw new BackendRequestError("response-too-large", "Backend response was too large.");
        }

        for (const item of Object.values(value)) {
            this.validateJsonDepthAndShape(item, depth + 1);
        }
    }

    private normalizeSetupStatus(raw: RawSetupStatus): SetupStatus {
        const enabled = Boolean(raw.enabled ?? raw.Enabled);
        const completed = Boolean(raw.completed ?? raw.Completed);
        const rawSteps = (raw.steps ?? raw.Steps ?? []) as unknown[];
        const steps: SetupStepStatus[] = [];

        for (const step of rawSteps) {
            if (!step || typeof step !== "object") continue;

            const details = step as {
                name?: unknown;
                Name?: unknown;
                state?: unknown;
                State?: unknown;
                reason?: unknown;
                Reason?: unknown;
                code?: unknown;
                Code?: unknown;
                serviceReady?: unknown;
                ServiceReady?: unknown;
            };

            const rawName = typeof details.name === "string" ? details.name : typeof details.Name === "string" ? details.Name : "setup";
            const normalizedName = rawName.toLowerCase();
            // Backend step names are a stable contract (including names such
            // as provider-indexers and arr-key-discovery). Preserve them
            // rather than collapsing safe, unknown names to "setup".
            const name = /^[a-z0-9][a-z0-9_-]{0,63}$/.test(normalizedName)
                ? normalizedName
                : "setup";
            const state = normalizeStepState(details.state ?? details.State);
            if (!state) continue;

            const serviceReady = typeof (details.serviceReady ?? details.ServiceReady) === "boolean"
                ? Boolean(details.serviceReady ?? details.ServiceReady)
                : undefined;
            steps.push({
                name,
                state,
                reason: normalizeSetupReason(details.reason ?? details.Reason),
                code: normalizeSetupReason(details.code ?? details.Code),
                serviceReady,
            });
        }

        const rawServices = (raw.services ?? raw.Services ?? []) as unknown[];
        const services: SetupServiceStatus[] = [];
        for (const service of rawServices) {
            if (!service || typeof service !== "object") continue;
            const details = service as { name?: unknown; Name?: unknown; ready?: unknown; Ready?: unknown; reason?: unknown; Reason?: unknown; code?: unknown; Code?: unknown };
            const rawName = typeof details.name === "string" ? details.name : typeof details.Name === "string" ? details.Name : "";
            const name = rawName.toLowerCase();
            if (!SAFE_SETUP_SERVICES.has(name) || typeof (details.ready ?? details.Ready) !== "boolean") continue;
            services.push({
                name,
                ready: Boolean(details.ready ?? details.Ready),
                reason: normalizeSetupReason(details.reason ?? details.Reason),
                code: normalizeSetupReason(details.code ?? details.Code),
            });
        }

        return {
            enabled,
            completed,
            steps,
            services,
            revocationPending: raw.revocationPending === true || raw.RevocationPending === true,
            repairRequired: raw.repairRequired === true || raw.RepairRequired === true,
        };
    }

    private normalizeSetupGrantResponse(raw: RawSetupGrantResponse): SetupGrantResponse {
        const grant = typeof raw.Grant === "string" ? raw.Grant : typeof raw.grant === "string" ? raw.grant : null;
        const expiresAtUtc = typeof raw.ExpiresAtUtc === "string"
            ? raw.ExpiresAtUtc
            : typeof raw.expiresAtUtc === "string"
                ? raw.expiresAtUtc
                : null;

        if (!grant || !expiresAtUtc) {
            throw new BackendRequestError("invalid-response", "Setup grant response was invalid.");
        }

        const revocationPending = typeof raw.RevocationPending === "boolean"
            ? raw.RevocationPending
            : typeof raw.revocationPending === "boolean"
                ? raw.revocationPending
                : undefined;
        const warning = typeof raw.Warning === "string"
            ? raw.Warning
            : typeof raw.warning === "string"
                ? raw.warning
                : undefined;
        const scope = raw.Scope === "Repair" || raw.scope === "repair" ? "repair" : "normal";

        const normalized: SetupGrantResponse = { grant, expiresAtUtc, scope };
        if (revocationPending !== undefined) normalized.revocationPending = revocationPending;
        if (warning !== undefined) normalized.warning = warning;
        return normalized;
    }

    private getSafeErrorMessage(context: string, status: number): string {
        if (status === 400) return `${context}: bad request.`;
        if (status === 401 || status === 403) return `${context}: access denied.`;
        if (status === 404) return `${context}: unavailable.`;
        if (status === 409) return `${context}: conflict.`;
        if (status === 429) return `${context}: too many attempts; retry later.`;
        if (status === 410) return `${context}: unavailable.`;
        if (status >= 500) return `${context}: backend unavailable.`;
        return `${context}: request failed.`;
    }

    private formatBackendError(data: BackendApiResponse): string | null {
        const allowlisted = new Map<string, string>([
            ["bad-request", "invalid request."],
            ["invalid", "request was invalid."],
            ["conflict", "request conflicts with current state."],
            ["not-found", "requested resource was not found."],
            ["forbidden", "access denied."],
            ["failed", "request failed."],
        ]);

        const code = typeof (data as BackendApiResponse & { code?: string | number }).code === "string"
            ? `${(data as BackendApiResponse & { code?: string | number }).code}`
            : typeof (data as BackendApiResponse & { code?: string | number }).code === "number"
                ? `${(data as BackendApiResponse & { code?: string | number }).code}`
                : undefined;

        if (code) {
            const mapped = allowlisted.get(code.toLowerCase());
            if (mapped) return `${mapped}`;
        }

        const errorCode = typeof data.Error === "string"
            ? data.Error
            : typeof data.message === "string"
                ? data.message
                : undefined;

        if (!errorCode) return null;

        const mappedCode = allowlisted.get(errorCode.toLowerCase());
        return mappedCode || null;
    }

    private getBackendActionCode(data: BackendApiResponse): BackendRequestErrorCode | undefined {
        const raw = typeof data.code === "string"
            ? data.code
            : typeof data.Error === "string"
                ? data.Error
                : typeof data.message === "string"
                    ? data.message
                    : undefined;
        switch (raw?.toLowerCase()) {
            case "bad-request":
            case "invalid":
                return "bad-request";
            case "conflict":
                return "conflict";
            case "rate-limited":
            case "too-many-attempts":
                return "rate-limited";
            case "not-found":
                return "not-found";
            case "forbidden":
                return "access-denied";
            case "failed":
                return "request-failed";
            default:
                return undefined;
        }
    }

    private baseUrl(path: string): string {
        return `${process.env.BACKEND_URL || ""}${path}`;
    }
}

export const backendClient = new BackendClient();

export type QueueResponse = {
    slots: QueueSlot[],
    noofslots: number,
}

export type QueueSlot = {
    nzo_id: string,
    priority: string,
    filename: string,
    cat: string,
    percentage: string,
    true_percentage: string,
    status: string,
    mb: string,
    mbleft: string,
}

export type HistoryResponse = {
    slots: HistorySlot[],
    noofslots: number,
}

export type HistorySlot = {
    nzo_id: string,
    nzb_name: string,
    name: string,
    category: string,
    status: string,
    bytes: number,
    storage: string,
    download_time: number,
    fail_message: string,
}

export type DirectoryItem = {
    name: string,
    isDirectory: boolean,
    size: number | null | undefined,
}

export type ConfigItem = {
    configName: string,
    configValue: string,
    hasSecret?: boolean,
    secretKind?: string,
}

const ADMIN_SETTINGS_CONFIG_KEYS = [
    "general.base-url",
    "api.categories",
    "api.manual-category",
    "api.ensure-importable-video",
    "api.ensure-article-existence-categories",
    "api.ignore-history-limit",
    "api.download-file-blocklist",
    "api.duplicate-nzb-behavior",
    "api.import-strategy",
    "api.completed-downloads-dir",
    "api.user-agent",
    "api.key",
    "usenet.max-download-connections",
    "usenet.streaming-priority",
    "usenet.article-buffer-size",
    "webdav.user",
    "webdav.pass",
    "webdav.show-hidden-files",
    "webdav.enforce-readonly",
    "webdav.preview-par2-files",
    "rclone.mount-dir",
    "media.library-dir",
    "arr.instances",
    "repair.enable",
    "cache.max-size-gb",
    "cache.max-age-hours",
    "cache.directory",
    "cache.precache-enable",
    "cache.precache-max-file-size-mb",
    "cache.read-ahead-enable",
    "cache.read-ahead-segments",
    "cache.l2.enabled",
    "cache.l2.endpoint",
    "cache.l2.bucket-name",
    "cache.l2.access-key",
    "cache.l2.secret-key",
    "cache.l2.ssl",
    "cache.metadata-shared-enabled",
    "cache.metadata-retention-days",
] as const;

type AdminSettingsConfigKey = typeof ADMIN_SETTINGS_CONFIG_KEYS[number];

export type AdminSettingsConfig = Record<AdminSettingsConfigKey, string> & {
    [key: string]: string,
};

const ADMIN_SETTINGS_SECRET_KEYS = [
    "api.key",
    "webdav.pass",
    "cache.l2.access-key",
    "cache.l2.secret-key",
    "arr.instances",
] as const;

const ADMIN_SETTINGS_REDIRECTED_KEYS = [
    "api.key",
    "webdav.pass",
    "cache.l2.access-key",
    "cache.l2.secret-key",
] as const;

type AdminSettingsSecretKey = typeof ADMIN_SETTINGS_SECRET_KEYS[number];
type AdminSettingsRedirectedConfigKey = typeof ADMIN_SETTINGS_REDIRECTED_KEYS[number];

export type AdminSettingsHasSecrets = Record<AdminSettingsSecretKey, boolean> & {
    [key: string]: boolean,
};

export type AdminSettingsResponse = {
    config: AdminSettingsConfig,
    hasSecrets: AdminSettingsHasSecrets,
}

export type AdminSettingsRequest = {
    config: Record<string, string>,
    clearSecrets?: string[],
}

export type UsenetProviderResponse = {
    id: string,
    host: string,
    port: number,
    ssl: boolean,
    user: string,
    max: number,
    type: number,
    hasPassword: boolean,
}

export type UsenetProviderRequest = Omit<UsenetProviderResponse, "hasPassword"> & {
    password?: string,
}

export type UsenetSettingsResponse = {
    providers: UsenetProviderResponse[],
    revision: string,
}

export type UsenetSettingsRequest = {
    revision: string,
    providers: UsenetProviderRequest[],
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

/** Convert untrusted backend JSON to a redacted response DTO. */
export function normalizeUsenetSettingsResponse(value: unknown): UsenetSettingsResponse {
    if (!isRecord(value) || typeof value.revision !== "string" || !Array.isArray(value.providers)) {
        throw new Error("Invalid Usenet settings response.");
    }
    const providers = value.providers.map(provider => {
        if (!isRecord(provider) || typeof provider.id !== "string" || typeof provider.host !== "string" ||
            !Number.isSafeInteger(provider.port) || typeof provider.ssl !== "boolean" ||
            typeof provider.user !== "string" || !Number.isSafeInteger(provider.max) ||
            !Number.isSafeInteger(provider.type) || typeof provider.hasPassword !== "boolean") {
            throw new Error("Invalid Usenet provider response.");
        }
        // Do not spread backend objects: a malicious `password` property is
        // intentionally omitted before this DTO can reach loader serialization.
        return {
            id: provider.id,
            host: provider.host,
            port: provider.port as number,
            ssl: provider.ssl,
            user: provider.user,
            max: provider.max as number,
            type: provider.type as number,
            hasPassword: provider.hasPassword,
        };
    });
    return { providers, revision: value.revision };
}

export type EncryptionStatus = {
    keySet: boolean,
    plaintextSecretsCount: number,
    bannerSeverity: "none" | "info" | "warning",
    migrationCompletedAt: string | null,
    postMigrationAcknowledged: boolean,
    postMigrationAcknowledgedAt: string | null,
}

export type TestUsenetConnectionRequest = {
    host: string,
    port: string,
    useSsl: string,
    user: string,
    pass: string
}

export type HealthCheckQueueResponse = {
    uncheckedCount: number,
    items: HealthCheckQueueItem[],
}

export type HealthCheckQueueItem = {
    id: string,
    name: string,
    path: string,
    releaseDate: string | null,
    lastHealthCheck: string | null,
    nextHealthCheck: string | null,
    progress: number,
}

export type HealthCheckHistoryResponse = {
    stats: HealthCheckStats[],
    items: HealthCheckResult[]
}

export type HealthResult = 0 | 1;

export type RepairAction = 0 | 1 | 2 | 3;

export type HealthCheckStats = {
    result: HealthResult,
    repairStatus: RepairAction,
    count: number
}

export type HealthCheckResult = {
    id: string,
    createdAt: string,
    davItemId: string,
    path: string,
    result: HealthResult,
    repairStatus: RepairAction,
    message: string | null
}

export type SetupStepState = "pending" | "running" | "complete" | "warning" | "failed";
export type SetupReasonCode =
    | "nzbdav-failed"
    | "sonarr-failed"
    | "radarr-failed"
    | "jellyfin-failed"
    | "provider-failed"
    | "provider-auth-failed"
    | "indexer-failed"
    | "indexer-capability-failed"
    | "configuration-failed"
    | "compatibility-failed"
    | "jellyfin-version-failed"
    | "library-collision"
    | "library-failed"
    | "task-failed"
    | "plugin-mismatch"
    | "arr-failed"
    | "prowlarr-failed"
    | "validation-canceled"
    | "backend-unavailable"
    | "setup-run-busy"
    | "unknown";

export type SetupStepStatus = {
    name: string,
    state: SetupStepState,
    reason?: SetupReasonCode,
    code?: SetupReasonCode,
    serviceReady?: boolean,
}

export type SetupServiceStatus = {
    name: string,
    ready: boolean,
    reason?: SetupReasonCode,
    code?: SetupReasonCode,
}

export type SetupStatus = {
    enabled: boolean,
    completed: boolean,
    steps: SetupStepStatus[],
    services: SetupServiceStatus[],
    revocationPending: boolean,
    repairRequired: boolean,
}

export type SetupRecoveryResponse = {
    revocationPending: boolean,
}

export type SetupGrantResponse = {
    grant: string,
    expiresAtUtc: string,
    scope?: "normal" | "repair",
    revocationPending?: boolean,
    warning?: string,
}

export type SetupUsenetProviderPayload = {
    Host: string,
    Port: number,
    UseSsl: boolean,
    User: string,
    Pass: string,
    MaxConnections: number,
    Type: number,
}

export type SetupIndexerPayload = {
    Name: string,
    Url: string,
    ApiKey: string,
    AllowPrivateNetwork?: boolean,
}

export type SetupConfigureRequest = {
    providers: SetupUsenetProviderPayload[],
    indexers: SetupIndexerPayload[],
}

type BackendApiResponse = {
    status?: boolean,
    error?: string,
    Error?: string,
    message?: string,
    code?: string | number,
    authenticated?: boolean,
    isOnboarding?: boolean,
    queue?: QueueSlot[] | { slots?: unknown; noofslots?: unknown },
    noofslots?: number,
    history?: HistorySlot[] | { slots?: unknown; noofslots?: unknown },
    configItems?: ConfigItem[],
    nzo_ids?: unknown[],
    slots?: unknown,
    items?: unknown,
    uncheckedCount?: number,
    [key: string]: unknown,
};

type BackendQueuePayload = {
    queue?: QueueSlot[] | { slots?: unknown; noofslots?: unknown },
    noofslots?: number,
    history?: HistorySlot[] | { slots?: unknown; noofslots?: unknown },
    [key: string]: unknown,
};

export function normalizeAdminSettingsResponse(raw: unknown): AdminSettingsResponse {
    if (!isRecord(raw)) {
        throw new Error("Invalid admin settings response.");
    }

    const configSource = raw.config;
    const hasSecretsSource = raw.hasSecrets;

    const config: AdminSettingsConfig = { ...EMPTY_ADMIN_SETTINGS_CONFIG };
    if (isRecord(configSource)) {
        for (const key of ADMIN_SETTINGS_CONFIG_KEYS) {
            const value = configSource[key];
            if (typeof value === "string") {
                config[key] = value;
            }
        }

        const sanitizedArr = normalizeArrInstances(configSource["arr.instances"]);
        if (sanitizedArr !== undefined) {
            config["arr.instances"] = sanitizedArr;
        }
    }

    for (const secretKey of ADMIN_SETTINGS_REDIRECTED_KEYS) {
        // Never trust backend values for write-only config fields.
        config[secretKey] = "";
    }

    const hasSecrets: AdminSettingsHasSecrets = { ...EMPTY_ADMIN_SETTINGS_HAS_SECRETS };
    if (isRecord(hasSecretsSource)) {
        for (const secretKey of ADMIN_SETTINGS_SECRET_KEYS) {
            hasSecrets[secretKey] = hasSecretsSource[secretKey] === true;
        }
    }

    return { config, hasSecrets };
}

const EMPTY_ADMIN_SETTINGS_CONFIG: AdminSettingsConfig = {
    "general.base-url": "",
    "api.categories": "",
    "api.manual-category": "",
    "api.ensure-importable-video": "",
    "api.ensure-article-existence-categories": "",
    "api.ignore-history-limit": "",
    "api.download-file-blocklist": "",
    "api.duplicate-nzb-behavior": "",
    "api.import-strategy": "",
    "api.completed-downloads-dir": "",
    "api.user-agent": "",
    "api.key": "",
    "usenet.max-download-connections": "",
    "usenet.streaming-priority": "",
    "usenet.article-buffer-size": "",
    "webdav.user": "",
    "webdav.pass": "",
    "webdav.show-hidden-files": "",
    "webdav.enforce-readonly": "",
    "webdav.preview-par2-files": "",
    "rclone.mount-dir": "",
    "media.library-dir": "",
    "arr.instances": "",
    "repair.enable": "",
    "cache.max-size-gb": "",
    "cache.max-age-hours": "",
    "cache.directory": "",
    "cache.precache-enable": "",
    "cache.precache-max-file-size-mb": "",
    "cache.read-ahead-enable": "",
    "cache.read-ahead-segments": "",
    "cache.l2.enabled": "",
    "cache.l2.endpoint": "",
    "cache.l2.bucket-name": "",
    "cache.l2.access-key": "",
    "cache.l2.secret-key": "",
    "cache.l2.ssl": "",
    "cache.metadata-shared-enabled": "",
    "cache.metadata-retention-days": "",
};

const EMPTY_ADMIN_SETTINGS_HAS_SECRETS: AdminSettingsHasSecrets = {
    "api.key": false,
    "webdav.pass": false,
    "cache.l2.access-key": false,
    "cache.l2.secret-key": false,
    "arr.instances": false,
};

type ArrInstancesShape = {
    RadarrInstances?: unknown,
    SonarrInstances?: unknown,
    QueueRules?: unknown,
};

type ArrInstanceShape = {
    Host?: unknown,
    ApiKey?: unknown,
    HasApiKey?: unknown,
};

type ArrQueueRuleShape = {
    Message?: unknown,
    Action?: unknown,
};

const normalizeArrInstances = (rawArrInstances: unknown): string | undefined => {
    if (typeof rawArrInstances !== "string") {
        return undefined;
    }

    try {
        const parsed = JSON.parse(rawArrInstances);
        if (!isRecord(parsed)) {
            return undefined;
        }

        const parsedArr = parsed as ArrInstancesShape;
        return JSON.stringify({
            RadarrInstances: Array.isArray(parsedArr.RadarrInstances)
                ? normalizeArrCollection(parsedArr.RadarrInstances)
                : [],
            SonarrInstances: Array.isArray(parsedArr.SonarrInstances)
                ? normalizeArrCollection(parsedArr.SonarrInstances)
                : [],
            QueueRules: Array.isArray(parsedArr.QueueRules)
                ? normalizeQueueRules(parsedArr.QueueRules)
                : [],
        });
    } catch {
        return undefined;
    }
};

const normalizeArrCollection = (rawInstances: unknown[]): Array<{ Host: string, ApiKey: string, HasApiKey?: boolean }> => {
    return rawInstances
        .filter((entry): entry is Record<string, unknown> => isRecord(entry))
        .map((entry) => {
            const typed = entry as ArrInstanceShape;
            return {
                Host: typeof typed.Host === "string" ? typed.Host : "",
                ApiKey: "",
                HasApiKey: typed.HasApiKey === true,
            };
        });
};

const normalizeQueueRules = (rawRules: unknown[]): Array<{ Message: string, Action: number }> => {
    return rawRules
        .filter((entry): entry is Record<string, unknown> => isRecord(entry))
        .map((entry) => {
            const typed = entry as ArrQueueRuleShape;
            return {
                Message: typeof typed.Message === "string" ? typed.Message : "",
                Action: Number.isInteger(typed.Action) ? typed.Action as number : 0,
            };
        });
};

type RawSetupRecoveryResponse = {
    revocationPending?: boolean,
    RevocationPending?: boolean,
};

type RawSetupGrantResponse = {
    grant?: string,
    Grant?: string,
    expiresAtUtc?: string,
    ExpiresAtUtc?: string,
    revocationPending?: boolean,
    RevocationPending?: boolean,
    warning?: string,
    Warning?: string,
    scope?: string,
    Scope?: string,
};

type RawSetupStatus = {
    enabled?: boolean,
    Enabled?: boolean,
    completed?: boolean,
    Completed?: boolean,
    steps?: unknown[],
    Steps?: unknown[],
    services?: unknown[],
    Services?: unknown[],
    revocationPending?: boolean,
    RevocationPending?: boolean,
    repairRequired?: boolean,
    RepairRequired?: boolean,
};

// provider is retained only for reading old persisted/status payloads; the
// backend emits the strict nzbdav,jellyfin,sonarr,radarr,prowlarr contract.
const SAFE_SETUP_SERVICES = new Set(["nzbdav", "jellyfin", "sonarr", "radarr", "prowlarr", "provider"]);

const normalizeSetupReason = (value: unknown): SetupReasonCode => {
    if (typeof value !== "string") return "unknown";
    switch (value.toLowerCase()) {
        case "nzbdav-failed":
            return "nzbdav-failed";
        case "sonarr-failed":
            return "sonarr-failed";
        case "radarr-failed":
            return "radarr-failed";
        case "jellyfin-failed":
            return "jellyfin-failed";
        case "provider-failed":
            return "provider-failed";
        case "provider-auth-failed":
            return "provider-auth-failed";
        case "indexer-failed":
            return "indexer-failed";
        case "indexer-capability-failed":
            return "indexer-capability-failed";
        case "configuration-failed":
            return "configuration-failed";
        case "compatibility-failed":
            return "compatibility-failed";
        case "jellyfin-version-failed":
            return "jellyfin-version-failed";
        case "library-collision":
            return "library-collision";
        case "library-failed":
            return "library-failed";
        case "task-failed":
            return "task-failed";
        case "plugin-mismatch":
            return "plugin-mismatch";
        case "arr-failed":
            return "arr-failed";
        case "prowlarr-failed":
            return "prowlarr-failed";
        case "validation-canceled":
            return "validation-canceled";
        case "backend-unavailable":
            return "backend-unavailable";
        case "unknown":
            return "unknown";
        default:
            return "unknown";
    }
};

const normalizeStepState = (value: unknown): SetupStepState | null => {
    if (typeof value !== "string") {
        return null;
    }

    switch (value.toLowerCase()) {
        case "pending":
            return "pending";
        case "running":
            return "running";
        case "complete":
            return "complete";
        case "warning":
            return "warning";
        case "failed":
            return "failed";
        default:
            return null;
    }
};

import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { Tabs, Tab, Button } from "react-bootstrap"
import { backendClient, type AdminSettingsResponse, type EncryptionStatus, type UsenetSettingsResponse } from "~/clients/backend-client.server";
import type { ConnectionDetails } from "./usenet/usenet";
import { isUsenetSettingsUpdated, UsenetSettings } from "./usenet/usenet";
import { isSabnzbdSettingsUpdated, isSabnzbdSettingsValid, SabnzbdSettings } from "./sabnzbd/sabnzbd";
import { isWebdavSettingsUpdated, isWebdavSettingsValid, WebdavSettings } from "./webdav/webdav";
import { isArrsSettingsUpdated, isArrsSettingsValid, ArrsSettings } from "./arrs/arrs";
import { Maintenance } from "./maintenance/maintenance";
import { isRepairsSettingsUpdated, RepairsSettings } from "./repairs/repairs";
import { useCallback, useState, type Dispatch, type SetStateAction } from "react";
import { useBlocker } from "react-router";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { csrfFetch } from "~/utils/csrf-fetch";
import { isAuthenticated } from "~/auth/authentication.server";
import { DEFAULT_NO_CACHE_HEADERS } from "~/onboarding/onboarding-request.server";

export const defaultConfig = {
    "general.base-url": "",
    "api.key": "",
    "api.categories": "",
    "api.manual-category": "uncategorized",
    "api.ensure-importable-video": "true",
    "api.ensure-article-existence-categories": "",
    "api.ignore-history-limit": "true",
    "api.download-file-blocklist": "*.nfo, *.par2, *.sfv, *sample.mkv",
    "api.duplicate-nzb-behavior": "increment",
    "api.import-strategy": "symlinks",
    "api.completed-downloads-dir": "",
    "api.user-agent": "",
    "usenet.max-download-connections": "15",
    "usenet.streaming-priority": "80",
    "usenet.article-buffer-size": "40",
    "webdav.user": "admin",
    "webdav.pass": "",
    "webdav.show-hidden-files": "false",
    "webdav.enforce-readonly": "true",
    "webdav.preview-par2-files": "false",
    "rclone.mount-dir": "",
    "media.library-dir": "",
    "arr.instances": "{\"RadarrInstances\":[],\"SonarrInstances\":[],\"QueueRules\":[]}",
    "repair.enable": "false",
    "cache.max-size-gb": "10",
    "cache.max-age-hours": "6",
    "cache.directory": "",
    "cache.precache-enable": "true",
    "cache.precache-max-file-size-mb": "5",
    "cache.read-ahead-enable": "true",
    "cache.read-ahead-segments": "200",
    "cache.l2.enabled": "false",
    "cache.l2.endpoint": "",
    "cache.l2.bucket-name": "nzbdav-segments",
    "cache.l2.access-key": "",
    "cache.l2.secret-key": "",
    "cache.l2.ssl": "false",
    "cache.metadata-shared-enabled": "true",
    "cache.metadata-retention-days": "90",
}

export async function loader({ request }: Route.LoaderArgs) {
    // React Router single-fetch can invoke this loader directly (for example
    // `settings.data?_routes=routes/settings`) without running root.loader.
    // Authenticate at the sensitive boundary before even constructing a
    // backend request; redirects carry no decrypted settings body.
    let authenticated = false;
    try {
        authenticated = await isAuthenticated(request);
    } catch {
        authenticated = false;
    }
    if (!authenticated) {
        // React Router may wrap a direct loader response in its single-fetch
        // envelope, but it must not get far enough to construct settings.
        return new Response(null, { status: 302, headers: { ...Object.fromEntries(new Headers(DEFAULT_NO_CACHE_HEADERS).entries()), Location: "/login", "Content-Length": "0" } });
    }

    const [adminSettings, usenetSettings, encryptionStatus] = await Promise.all([
        backendClient.getAdminSettings(),
        backendClient.getUsenetSettings(),
        backendClient.getEncryptionStatus()
    ]);

    const config: Record<string, string> = { ...defaultConfig, ...adminSettings.config };

    return {
        config: config,
        appVersion: process.env.NZBDAV_VERSION ?? "unknown",
        encryptionStatus,
        usenetSettings,
        hasSecrets: adminSettings.hasSecrets,
    }
}

export default function Settings(props: Route.ComponentProps) {
    return (
        <Body {...props.loaderData} />
    );
}

export const WRITE_ONLY_SECRET_KEYS = ["api.key", "webdav.pass", "cache.l2.access-key", "cache.l2.secret-key", "arr.instances"] as const;
type WriteOnlySecretKey = typeof WRITE_ONLY_SECRET_KEYS[number];

type BodyProps = {
    config: Record<string, string>,
    appVersion: string,
    encryptionStatus: EncryptionStatus,
    usenetSettings: UsenetSettingsResponse,
    hasSecrets: Record<string, boolean>,
};

export function Body(props: BodyProps) {
    // stateful variables
    const [config, setConfig] = useState(props.config);
    const [newConfig, setNewConfig] = useState(config);
    const [usenetSettings, setUsenetSettings] = useState(props.usenetSettings);
    const [newUsenetProviders, setNewUsenetProviders] = useState<ConnectionDetails[]>(
        toProviderDrafts(props.usenetSettings.providers));
    const [isSaving, setIsSaving] = useState(false);
    const [isSaved, setIsSaved] = useState(false);
    const [saveError, setSaveError] = useState<string | null>(null);
    const [clearSecrets, setClearSecrets] = useState<Set<string>>(new Set());
    const [hasSecrets, setHasSecrets] = useState(props.hasSecrets);
    const [activeTab, setActiveTab] = useState('usenet');
    const [postMigrationAcknowledged, setPostMigrationAcknowledged] = useState(
        props.encryptionStatus.postMigrationAcknowledged);
    const [isAcknowledgingPostMigration, setIsAcknowledgingPostMigration] = useState(false);

    // derived variables
    const iseUsenetUpdated = isUsenetSettingsUpdated(
        toProviderDrafts(usenetSettings.providers), newUsenetProviders);
    const isSabnzbdUpdated = isSabnzbdSettingsUpdated(config, newConfig);
    const isWebdavUpdated = isWebdavSettingsUpdated(config, newConfig);
    const isArrsUpdated = isArrsSettingsUpdated(config, newConfig);
    const isRepairsUpdated = isRepairsSettingsUpdated(config, newConfig);
    const isUpdated = iseUsenetUpdated || isSabnzbdUpdated || isWebdavUpdated || isArrsUpdated || isRepairsUpdated || clearSecrets.size > 0;
    const navigationBlocker = useNavigationBlocker(isUpdated);
    const showEncryptionBanner = props.encryptionStatus.bannerSeverity !== "none";
    const showPostMigrationBanner = props.encryptionStatus.migrationCompletedAt !== null && !postMigrationAcknowledged;

    const usenetTitle = iseUsenetUpdated ? "✏️ Usenet" : "Usenet";
    const sabnzbdTitle = isSabnzbdUpdated ? "✏️ SABnzbd " : "SABnzbd";
    const webdavTitle = isWebdavUpdated ? "✏️ WebDAV" : "WebDAV";
    const arrsTitle = isArrsUpdated ? "✏️ Radarr/Sonarr" : "Radarr/Sonarr";
    const repairsTitle = isRepairsUpdated ? "✏️ Repairs" : "Repairs";

    const saveButtonLabel = isSaving ? "Saving..."
        : !isUpdated && isSaved ? "Saved ✅"
        : !isUpdated && !isSaved ? "There are no changes to save"
        : isSabnzbdUpdated && !isSabnzbdSettingsValid(newConfig) ? "Invalid SABnzbd settings"
        : isWebdavUpdated && !isWebdavSettingsValid(newConfig, hasSecrets) ? "Invalid WebDAV settings"
        : isArrsUpdated && !isArrsSettingsValid(newConfig) ? "Invalid Arrs settings"
        : "Save";
    const saveButtonVariant = saveButtonLabel === "Save" ? "primary"
        : saveButtonLabel === "Saved ✅" ? "success"
        : "secondary";
    const isSaveButtonDisabled = saveButtonLabel !== "Save";

    // events
    const onClear = useCallback(() => {
        setNewConfig(config);
        setNewUsenetProviders(toProviderDrafts(usenetSettings.providers));
        setClearSecrets(new Set());
        setIsSaved(false);
        setSaveError(null);
    }, [config, usenetSettings, setNewConfig]);

    const onSave = useCallback(async () => {
        setIsSaving(true);
        setIsSaved(false);
        setSaveError(null);
        try {
            let nextUsenetSettings = usenetSettings;
            let nextUsenetProviders = newUsenetProviders;

            if (iseUsenetUpdated) {
                const usenetResponse = await csrfFetch("/settings/usenet", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(buildUsenetRequestPayload(usenetSettings.revision, newUsenetProviders)),
                }, { idempotent: true });
                if (!usenetResponse.ok) {
                    throw new Error("Unable to save Usenet settings.");
                }
                nextUsenetSettings = await usenetResponse.json() as typeof usenetSettings;
                nextUsenetProviders = mergeUsenetDraftWithSavedPasses(nextUsenetSettings, nextUsenetProviders);
                setUsenetSettings(nextUsenetSettings);
                setNewUsenetProviders(nextUsenetProviders);
            }

            const response = await csrfFetch("/settings/update", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    config: getChangedConfig(config, newConfig, clearSecrets),
                    clearSecrets: [...clearSecrets],
                }),
            }, { idempotent: true });
            if (!response.ok) throw new Error("Unable to save settings.");
            const saved = await response.json() as AdminSettingsResponse;
            const persistedConfig = { ...newConfig, ...(saved.config || {}) };
            setConfig(persistedConfig);
            setNewConfig(persistedConfig);
            setHasSecrets(deriveHasSecrets(saved, hasSecrets));
            setUsenetSettings(nextUsenetSettings);
            setNewUsenetProviders(toProviderDrafts(nextUsenetSettings.providers));
            setClearSecrets(new Set());
            setIsSaved(true);
        } catch {
            setIsSaved(false);
            setSaveError("Settings could not be saved. Please try again.");
        } finally {
            setIsSaving(false);
        }
    }, [config, hasSecrets, clearSecrets, newConfig, iseUsenetUpdated, newUsenetProviders, usenetSettings]);


    const onAcknowledgePostMigration = useCallback(async () => {
        setIsAcknowledgingPostMigration(true);
        setSaveError(null);
        try {
            const response = await csrfFetch("/settings/acknowledge-post-migration", {
                method: "POST"
            });
            if (!response.ok) {
                setSaveError("Unable to dismiss the migration banner. Please try again.");
                return;
            }
            setPostMigrationAcknowledged(true);
        } catch {
            setSaveError("Unable to dismiss the migration banner. Please try again.");
        } finally {
            setIsAcknowledgingPostMigration(false);
        }
    }, []);

    return (
        <div className={styles.container}>
            {showEncryptionBanner && (
                <div className={`${styles.banner} ${props.encryptionStatus.bannerSeverity === "warning" ? styles.bannerWarning : styles.bannerInfo}`}>
                    <span className={styles.bannerTitle}>Encryption At Rest</span>
                    {props.encryptionStatus.bannerSeverity === "warning"
                        ? `Sensitive settings are still stored in plaintext. Set NZBDAV_MASTER_KEY to encrypt them at rest. ${props.encryptionStatus.plaintextSecretsCount} plaintext secret value(s) are currently stored in the config database.`
                        : "NZBDAV_MASTER_KEY is not configured yet. New installations should set it before storing long-lived secrets."}
                </div>
            )}
            {showPostMigrationBanner && (
                <div className={`${styles.banner} ${styles.bannerWarning}`}>
                    <span className={styles.bannerTitle}>Historical Backups Need Credential Rotation</span>
                    Encryption was enabled on {formatTimestamp(props.encryptionStatus.migrationCompletedAt)}. Older backups of the config database remain plaintext. Rotate usenet provider passwords, Radarr/Sonarr API keys, the NZBDAV API key, and any copied WebDAV credentials if those backups might exist outside your control.
                    <div>
                        <Button
                            className={styles.bannerButton}
                            variant="outline-light"
                            disabled={isAcknowledgingPostMigration}
                            onClick={onAcknowledgePostMigration}>
                            {isAcknowledgingPostMigration
                                ? "Saving..."
                                : "I've rotated my credentials - dismiss"}
                        </Button>
                    </div>
                </div>
            )}
            <Tabs
                activeKey={activeTab}
                onSelect={x => setActiveTab(x!)}
                className={styles.tabs}
            >
                <Tab eventKey="usenet" title={usenetTitle}>
                    <UsenetSettings providers={newUsenetProviders} setProviders={setNewUsenetProviders} />
                </Tab>
                <Tab eventKey="sabnzbd" title={sabnzbdTitle}>
                    <SabnzbdSettings config={newConfig} setNewConfig={setNewConfig} appVersion={props.appVersion} hasSecrets={hasSecrets} clearSecrets={clearSecrets} onSecretChange={setSecretClearState(setClearSecrets)} />
                </Tab>
                <Tab eventKey="webdav" title={webdavTitle}>
                    <WebdavSettings config={newConfig} setNewConfig={setNewConfig} hasSecrets={hasSecrets} clearSecrets={clearSecrets} onSecretChange={setSecretClearState(setClearSecrets)} />
                </Tab>
                <Tab eventKey="arrs" title={arrsTitle}>
                    <ArrsSettings config={newConfig} setNewConfig={setNewConfig} hasSecrets={hasSecrets} clearSecrets={clearSecrets} onSecretChange={setSecretClearState(setClearSecrets)} />
                </Tab>
                <Tab eventKey="repairs" title={repairsTitle}>
                    <RepairsSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="maintenance" title="Maintenance">
                    <Maintenance savedConfig={config} />
                </Tab>
            </Tabs>
            <hr />
            {saveError && <div role="alert" className={styles.bannerWarning}>{saveError}</div>}
            {isUpdated && <Button
                className={styles.button}
                variant="secondary"
                disabled={!isUpdated}
                onClick={() => onClear()}>
                Clear
            </Button>}
            <Button
                className={styles.button}
                variant={saveButtonVariant}
                disabled={isSaveButtonDisabled}
                onClick={onSave}>
                {saveButtonLabel}
            </Button>
            <ConfirmModal
                show={navigationBlocker.showConfirmation}
                title="Unsaved Changes"
                message={<>You have unsaved changes.<br/>Are you sure you want to leave this page?</>}
                cancelText="Stay"
                confirmText="Leave"
                onCancel={navigationBlocker.onCancelNavigation}
                onConfirm={navigationBlocker.onConfirmNavigation}
            />
        </div>
    );
}

function toProviderDrafts(providers: UsenetSettingsResponse["providers"]): ConnectionDetails[] {
    return providers.map(provider => ({
        Id: provider.id,
        Host: provider.host,
        Port: provider.port,
        UseSsl: provider.ssl,
        User: provider.user,
        MaxConnections: provider.max,
        Type: provider.type as ConnectionDetails["Type"],
        Pass: "",
        HasPassword: provider.hasPassword,
    }));
}

function toProviderRequest(provider: ConnectionDetails) {
    return {
        id: provider.Id,
        host: provider.Host,
        port: provider.Port,
        ssl: provider.UseSsl,
        user: provider.User,
        max: provider.MaxConnections,
        type: provider.Type,
        ...(provider.Pass ? { password: provider.Pass } : {}),
    };
}

export function mergeUsenetDraftWithSavedPasses(savedUsenet: UsenetSettingsResponse, draftProviders: ConnectionDetails[]): ConnectionDetails[] {
    return toProviderDrafts(savedUsenet.providers).map((provider, index) => ({
        ...provider,
        Pass: draftProviders[index]?.Pass || "",
    }));
}

export function buildUsenetRequestPayload(revision: string, providers: ConnectionDetails[]) {
    return {
        revision,
        providers: providers.map(toProviderRequest),
    };
}

export function getChangedConfig(
    config: Record<string, string>,
    newConfig: Record<string, string>,
    clearSecrets: ReadonlySet<string> = new Set(),
): Record<string, string> {
    const changedConfig: Record<string, string> = {};
    for (const configKey of Object.keys(defaultConfig)) {
        // A redacted write-only field is intentionally blank. Clearing is a
        // separate explicit operation; blank input must preserve the secret.
        if (clearSecrets.has(configKey)) continue;
        if (config[configKey] !== newConfig[configKey]) changedConfig[configKey] = newConfig[configKey];
    }
    return changedConfig;
}

export function setSecretClearState(setter: Dispatch<SetStateAction<Set<string>>>) {
    return (key: WriteOnlySecretKey, clear: boolean) => {
        setter(previous => {
            const next = new Set(previous);
            if (clear) next.add(key); else next.delete(key);
            return next;
        });
    };
}

function formatTimestamp(value: string | null) {
    if (value === null) return "an unknown date";

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

export function deriveHasSecrets(saved: AdminSettingsResponse, fallback: Record<string, boolean>): Record<string, boolean> {
    return saved.hasSecrets ?? fallback;
}

function useNavigationBlocker(isConfigUpdated: boolean) {
    const blocker = useBlocker(isConfigUpdated);

    const onConfirmNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.proceed();
        }
    }, [blocker]);

    const onCancelNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.reset();
        }
    }, [blocker]);

    return {
        showConfirmation: blocker.state === "blocked",
        onConfirmNavigation,
        onCancelNavigation
    }
}

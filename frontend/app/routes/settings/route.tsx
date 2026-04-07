import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { Tabs, Tab, Button } from "react-bootstrap"
import { backendClient, type EncryptionStatus } from "~/clients/backend-client.server";
import { isUsenetSettingsUpdated, UsenetSettings } from "./usenet/usenet";
import { isSabnzbdSettingsUpdated, isSabnzbdSettingsValid, SabnzbdSettings } from "./sabnzbd/sabnzbd";
import { isWebdavSettingsUpdated, isWebdavSettingsValid, WebdavSettings } from "./webdav/webdav";
import { isArrsSettingsUpdated, isArrsSettingsValid, ArrsSettings } from "./arrs/arrs";
import { Maintenance } from "./maintenance/maintenance";
import { isRepairsSettingsUpdated, RepairsSettings } from "./repairs/repairs";
import { useCallback, useState } from "react";
import { useBlocker } from "react-router";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";

const defaultConfig = {
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
    "usenet.providers": "",
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
    const [configItems, encryptionStatus] = await Promise.all([
        backendClient.getConfig(Object.keys(defaultConfig)),
        backendClient.getEncryptionStatus()
    ]);

    // transform to a map
    const config: Record<string, string> = { ...defaultConfig };
    for (const item of configItems) {
        config[item.configName] = item.configValue;
    }

    return {
        config: config,
        appVersion: process.env.NZBDAV_VERSION ?? "unknown",
        encryptionStatus,
    }
}

export default function Settings(props: Route.ComponentProps) {
    return (
        <Body {...props.loaderData} />
    );
}

type BodyProps = {
    config: Record<string, string>,
    appVersion: string,
    encryptionStatus: EncryptionStatus,
};

function Body(props: BodyProps) {
    // stateful variables
    const [config, setConfig] = useState(props.config);
    const [newConfig, setNewConfig] = useState(config);
    const [isSaving, setIsSaving] = useState(false);
    const [isSaved, setIsSaved] = useState(false);
    const [activeTab, setActiveTab] = useState('usenet');
    const [postMigrationAcknowledged, setPostMigrationAcknowledged] = useState(
        props.encryptionStatus.postMigrationAcknowledged !== null);
    const [isAcknowledgingPostMigration, setIsAcknowledgingPostMigration] = useState(false);

    // derived variables
    const iseUsenetUpdated = isUsenetSettingsUpdated(config, newConfig);
    const isSabnzbdUpdated = isSabnzbdSettingsUpdated(config, newConfig);
    const isWebdavUpdated = isWebdavSettingsUpdated(config, newConfig);
    const isArrsUpdated = isArrsSettingsUpdated(config, newConfig);
    const isRepairsUpdated = isRepairsSettingsUpdated(config, newConfig);
    const isUpdated = iseUsenetUpdated || isSabnzbdUpdated || isWebdavUpdated || isArrsUpdated || isRepairsUpdated;
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
        : isWebdavUpdated && !isWebdavSettingsValid(newConfig) ? "Invalid WebDAV settings"
        : isArrsUpdated && !isArrsSettingsValid(newConfig) ? "Invalid Arrs settings"
        : "Save";
    const saveButtonVariant = saveButtonLabel === "Save" ? "primary"
        : saveButtonLabel === "Saved ✅" ? "success"
        : "secondary";
    const isSaveButtonDisabled = saveButtonLabel !== "Save";

    // events
    const onClear = useCallback(() => {
        setNewConfig(config);
        setIsSaved(false);
    }, [config, setNewConfig]);

    const onSave = useCallback(async () => {
        setIsSaving(true);
        setIsSaved(false);
        const response = await fetch("/settings/update", {
            method: "POST",
            body: (() => {
                const form = new FormData();
                const changedConfig = getChangedConfig(config, newConfig);
                form.append("config", JSON.stringify(changedConfig));
                return form;
            })()
        });
        if (response.ok) {
            setConfig(newConfig);
        }
        setIsSaving(false);
        setIsSaved(true);
    }, [config, newConfig, setIsSaving, setIsSaved, setConfig]);

    const onAcknowledgePostMigration = useCallback(async () => {
        setIsAcknowledgingPostMigration(true);
        const response = await fetch("/settings/acknowledge-post-migration", {
            method: "POST"
        });
        if (response.ok) {
            setPostMigrationAcknowledged(true);
        }
        setIsAcknowledgingPostMigration(false);
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
                    <UsenetSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="sabnzbd" title={sabnzbdTitle}>
                    <SabnzbdSettings config={newConfig} setNewConfig={setNewConfig} appVersion={props.appVersion} />
                </Tab>
                <Tab eventKey="webdav" title={webdavTitle}>
                    <WebdavSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="arrs" title={arrsTitle}>
                    <ArrsSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="repairs" title={repairsTitle}>
                    <RepairsSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="maintenance" title="Maintenance">
                    <Maintenance savedConfig={config} />
                </Tab>
            </Tabs>
            <hr />
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

function getChangedConfig(
    config: Record<string, string>,
    newConfig: Record<string, string>
): Record<string, string> {
    let changedConfig: Record<string, string> = {};
    let configKeys = Object.keys(defaultConfig);
    for (const configKey of configKeys) {
        if (config[configKey] !== newConfig[configKey]) {
            changedConfig[configKey] = newConfig[configKey];
        }
    }
    return changedConfig;
}

function formatTimestamp(value: string | null) {
    if (value === null) return "an unknown date";

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
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

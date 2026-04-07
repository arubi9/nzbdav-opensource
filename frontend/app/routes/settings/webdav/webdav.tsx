import { Form, InputGroup } from "react-bootstrap";
import styles from "./webdav.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Label htmlFor="webdav-user-input">WebDAV User</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidUser(config["webdav.user"]) && styles.error])}
                    type="text"
                    id="webdav-user-input"
                    aria-describedby="webdav-user-help"
                    placeholder="admin"
                    value={config["webdav.user"]}
                    onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                <Form.Text id="webdav-user-help" muted>
                    Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="webdav-pass-input">WebDAV Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="webdav-pass-input"
                    aria-describedby="webdav-pass-help"
                    value={config["webdav.pass"]}
                    onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
                <Form.Text id="webdav-pass-help" muted>
                    Use this password to connect to the webdav.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="max-download-connections-input">Max Download Connections</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) && styles.error])}
                    type="text"
                    id="max-download-connections-input"
                    aria-describedby="max-download-connections-help"
                    placeholder="15"
                    value={config["usenet.max-download-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })} />
                <Form.Text id="max-download-connections-help" muted>
                    The maximum number of connections that will be used for downloading articles from your usenet provider(s).
                    Configure this to the minimum number of connections that will fully saturate your server's bandwidth.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="streaming-priority-input">Streaming Priority (vs Queue)</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        className={!isValidStreamingPriority(config["usenet.streaming-priority"]) ? styles.error : undefined}
                        type="text"
                        id="streaming-priority-input"
                        aria-describedby="streaming-priority-help"
                        placeholder="80"
                        value={config["usenet.streaming-priority"]}
                        onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                    <InputGroup.Text>%</InputGroup.Text>
                </InputGroup>
                <Form.Text id="streaming-priority-help" muted>
                    When streaming from the webdav while the queue is also active, how much bandwidth should be dedicated to streaming?
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="article-buffer-size-input">Article Buffer Size</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidArticleBufferSize(config["usenet.article-buffer-size"]) && styles.error])}
                    type="text"
                    id="article-buffer-size-input"
                    aria-describedby="article-buffer-size-help"
                    placeholder="40"
                    value={config["usenet.article-buffer-size"]}
                    onChange={e => setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })} />
                <Form.Text id="article-buffer-size-help" muted>
                    The number of articles to buffer ahead, per stream, when reading from the webdav.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="cache-max-size-input">Cache Max Size (GB)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isPositiveInteger(config["cache.max-size-gb"]) && styles.error])}
                    type="text"
                    id="cache-max-size-input"
                    aria-describedby="cache-max-size-help"
                    placeholder="10"
                    value={config["cache.max-size-gb"]}
                    onChange={e => setNewConfig({ ...config, "cache.max-size-gb": e.target.value })} />
                <Form.Text id="cache-max-size-help" muted>
                    Maximum disk space used by the streaming segment cache.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="cache-max-age-input">Cache Max Age (Hours)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isPositiveInteger(config["cache.max-age-hours"]) && styles.error])}
                    type="text"
                    id="cache-max-age-input"
                    aria-describedby="cache-max-age-help"
                    placeholder="6"
                    value={config["cache.max-age-hours"]}
                    onChange={e => setNewConfig({ ...config, "cache.max-age-hours": e.target.value })} />
                <Form.Text id="cache-max-age-help" muted>
                    Cached segments older than this are eligible for eviction.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="cache-directory-input">Cache Directory (Optional)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="cache-directory-input"
                    aria-describedby="cache-directory-help"
                    value={config["cache.directory"]}
                    onChange={e => setNewConfig({ ...config, "cache.directory": e.target.value })} />
                <Form.Text id="cache-directory-help" muted>
                    Leave empty for default location. Changing this requires a restart.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="precache-checkbox"
                    aria-describedby="precache-help"
                    label="Pre-cache Small Files"
                    checked={config["cache.precache-enable"] === "true"}
                    onChange={e => setNewConfig({ ...config, "cache.precache-enable": "" + e.target.checked })} />
                <Form.Text id="precache-help" muted>
                    Automatically cache small files (posters, subtitles, NFOs) after NZB processing for instant playback.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="precache-max-file-size-input">Pre-cache Max File Size (MB)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isPositiveInteger(config["cache.precache-max-file-size-mb"]) && styles.error])}
                    type="text"
                    id="precache-max-file-size-input"
                    aria-describedby="precache-max-file-size-help"
                    placeholder="5"
                    value={config["cache.precache-max-file-size-mb"]}
                    onChange={e => setNewConfig({ ...config, "cache.precache-max-file-size-mb": e.target.value })} />
                <Form.Text id="precache-max-file-size-help" muted>
                    Files at or below this size are eligible for post-download pre-caching.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="read-ahead-checkbox"
                    aria-describedby="read-ahead-help"
                    label="Read-ahead Warming"
                    checked={config["cache.read-ahead-enable"] === "true"}
                    onChange={e => setNewConfig({ ...config, "cache.read-ahead-enable": "" + e.target.checked })} />
                <Form.Text id="read-ahead-help" muted>
                    Pre-fetch video segments ahead of playback into the cache, freeing connections for other users.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="read-ahead-segments-input">Read-ahead Segments</Form.Label>
                <Form.Control
                    {...className([styles.input, !isPositiveInteger(config["cache.read-ahead-segments"]) && styles.error])}
                    type="text"
                    id="read-ahead-segments-input"
                    aria-describedby="read-ahead-segments-help"
                    placeholder="200"
                    value={config["cache.read-ahead-segments"]}
                    onChange={e => setNewConfig({ ...config, "cache.read-ahead-segments": e.target.value })} />
                <Form.Text id="read-ahead-segments-help" muted>
                    The maximum number of upcoming segments to warm into cache for an active stream.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="l2-enabled-checkbox"
                    aria-describedby="l2-enabled-help"
                    label="Enable Shared L2 Object Cache"
                    checked={config["cache.l2.enabled"] === "true"}
                    onChange={e => setNewConfig({ ...config, "cache.l2.enabled": "" + e.target.checked })} />
                <Form.Text id="l2-enabled-help" muted>
                    Store segment bodies in shared object storage so other nodes can reuse them after an L1 miss.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="l2-endpoint-input">L2 Endpoint</Form.Label>
                <Form.Control
                    {...className([styles.input, isL2Enabled(config) && !isNonEmpty(config["cache.l2.endpoint"]) && styles.error])}
                    type="text"
                    id="l2-endpoint-input"
                    aria-describedby="l2-endpoint-help"
                    placeholder="minio:9000"
                    value={config["cache.l2.endpoint"]}
                    onChange={e => setNewConfig({ ...config, "cache.l2.endpoint": e.target.value })} />
                <Form.Text id="l2-endpoint-help" muted>
                    S3-compatible endpoint for shared cache storage. Example: <code>minio:9000</code>.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="l2-bucket-name-input">L2 Bucket Name</Form.Label>
                <Form.Control
                    {...className([styles.input, isL2Enabled(config) && !isValidL2BucketName(config["cache.l2.bucket-name"]) && styles.error])}
                    type="text"
                    id="l2-bucket-name-input"
                    aria-describedby="l2-bucket-name-help"
                    placeholder="nzbdav-segments"
                    value={config["cache.l2.bucket-name"]}
                    onChange={e => setNewConfig({ ...config, "cache.l2.bucket-name": e.target.value })} />
                <Form.Text id="l2-bucket-name-help" muted>
                    Bucket names must be 3-63 characters of lowercase letters, numbers, and dashes.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="l2-access-key-input">L2 Access Key</Form.Label>
                <Form.Control
                    {...className([styles.input, isL2Enabled(config) && !isNonEmpty(config["cache.l2.access-key"]) && styles.error])}
                    type="text"
                    id="l2-access-key-input"
                    aria-describedby="l2-access-key-help"
                    value={config["cache.l2.access-key"]}
                    onChange={e => setNewConfig({ ...config, "cache.l2.access-key": e.target.value })} />
                <Form.Text id="l2-access-key-help" muted>
                    Matches the MinIO root user or the access key for your S3-compatible backend.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="l2-secret-key-input">L2 Secret Key</Form.Label>
                <Form.Control
                    {...className([styles.input, isL2Enabled(config) && !isNonEmpty(config["cache.l2.secret-key"]) && styles.error])}
                    type="password"
                    id="l2-secret-key-input"
                    aria-describedby="l2-secret-key-help"
                    value={config["cache.l2.secret-key"]}
                    onChange={e => setNewConfig({ ...config, "cache.l2.secret-key": e.target.value })} />
                <Form.Text id="l2-secret-key-help" muted>
                    Secret used with the access key to authenticate against shared object storage.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="l2-ssl-checkbox"
                    aria-describedby="l2-ssl-help"
                    label="Use SSL for L2"
                    checked={config["cache.l2.ssl"] === "true"}
                    onChange={e => setNewConfig({ ...config, "cache.l2.ssl": "" + e.target.checked })} />
                <Form.Text id="l2-ssl-help" muted>
                    Enable this only when the object storage endpoint requires TLS.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="readonly-checkbox"
                    aria-describedby="readonly-help"
                    label={`Enforce Read-Only`}
                    checked={config["webdav.enforce-readonly"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })} />
                <Form.Text id="readonly-help" muted>
                    The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="show-hidden-files-checkbox"
                    aria-describedby="show-hidden-files-help"
                    label={`Show hidden files on Dav Explorer`}
                    checked={config["webdav.show-hidden-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })} />
                <Form.Text id="show-hidden-files-help" muted>
                    Hidden files or directories are those whose names are prefixed by a period.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="preview-par2-files-checkbox"
                    aria-describedby="preview-par2-files-help"
                    label={`Preview par2 files on Dav Explorer`}
                    checked={config["webdav.preview-par2-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })} />
                <Form.Text id="preview-par2-files-help" muted>
                    When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
        || config["cache.max-size-gb"] !== newConfig["cache.max-size-gb"]
        || config["cache.max-age-hours"] !== newConfig["cache.max-age-hours"]
        || config["cache.directory"] !== newConfig["cache.directory"]
        || config["cache.precache-enable"] !== newConfig["cache.precache-enable"]
        || config["cache.precache-max-file-size-mb"] !== newConfig["cache.precache-max-file-size-mb"]
        || config["cache.read-ahead-enable"] !== newConfig["cache.read-ahead-enable"]
        || config["cache.read-ahead-segments"] !== newConfig["cache.read-ahead-segments"]
        || config["cache.l2.enabled"] !== newConfig["cache.l2.enabled"]
        || config["cache.l2.endpoint"] !== newConfig["cache.l2.endpoint"]
        || config["cache.l2.bucket-name"] !== newConfig["cache.l2.bucket-name"]
        || config["cache.l2.access-key"] !== newConfig["cache.l2.access-key"]
        || config["cache.l2.secret-key"] !== newConfig["cache.l2.secret-key"]
        || config["cache.l2.ssl"] !== newConfig["cache.l2.ssl"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    return isValidUser(newConfig["webdav.user"])
        && isValidMaxDownloadConnections(newConfig["usenet.max-download-connections"])
        && isValidStreamingPriority(newConfig["usenet.streaming-priority"])
        && isValidArticleBufferSize(newConfig["usenet.article-buffer-size"])
        && isPositiveInteger(newConfig["cache.max-size-gb"])
        && isPositiveInteger(newConfig["cache.max-age-hours"])
        && isPositiveInteger(newConfig["cache.precache-max-file-size-mb"])
        && isPositiveInteger(newConfig["cache.read-ahead-segments"])
        && isValidL2Settings(newConfig);
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isValidMaxDownloadConnections(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidStreamingPriority(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 100;
}

function isValidArticleBufferSize(value: string): boolean {
    return isPositiveInteger(value);
}

function isL2Enabled(config: Record<string, string>): boolean {
    return config["cache.l2.enabled"] === "true";
}

function isValidL2Settings(config: Record<string, string>): boolean {
    if (!isL2Enabled(config)) {
        return true;
    }

    return isNonEmpty(config["cache.l2.endpoint"])
        && isValidL2BucketName(config["cache.l2.bucket-name"])
        && isNonEmpty(config["cache.l2.access-key"])
        && isNonEmpty(config["cache.l2.secret-key"]);
}

function isValidL2BucketName(value: string): boolean {
    const regex = /^[a-z0-9-]{3,63}$/;
    return regex.test(value);
}

function isNonEmpty(value: string): boolean {
    return value.trim() !== "";
}

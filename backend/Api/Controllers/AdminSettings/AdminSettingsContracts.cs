using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.AdminSettings;

public sealed class AdminSettingsResponse
{
    public AdminSettingsConfig Config { get; set; } = new();
    public AdminSettingsHasSecrets HasSecrets { get; set; } = new();
}

public sealed class AdminSettingsConfig
{
    [JsonPropertyName("general.base-url")]
    public string GeneralBaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("api.categories")]
    public string ApiCategories { get; set; } = string.Empty;

    [JsonPropertyName("api.manual-category")]
    public string ApiManualCategory { get; set; } = string.Empty;

    [JsonPropertyName("api.ensure-importable-video")]
    public string ApiEnsureImportableVideo { get; set; } = string.Empty;

    [JsonPropertyName("api.ensure-article-existence-categories")]
    public string ApiEnsureArticleExistenceCategories { get; set; } = string.Empty;

    [JsonPropertyName("api.ignore-history-limit")]
    public string ApiIgnoreHistoryLimit { get; set; } = string.Empty;

    [JsonPropertyName("api.download-file-blocklist")]
    public string ApiDownloadFileBlocklist { get; set; } = string.Empty;

    [JsonPropertyName("api.duplicate-nzb-behavior")]
    public string ApiDuplicateNzbBehavior { get; set; } = string.Empty;

    [JsonPropertyName("api.import-strategy")]
    public string ApiImportStrategy { get; set; } = string.Empty;

    [JsonPropertyName("api.completed-downloads-dir")]
    public string ApiCompletedDownloadsDir { get; set; } = string.Empty;

    [JsonPropertyName("api.user-agent")]
    public string ApiUserAgent { get; set; } = string.Empty;

    [JsonPropertyName("usenet.max-download-connections")]
    public string UsenetMaxDownloadConnections { get; set; } = string.Empty;

    [JsonPropertyName("usenet.streaming-priority")]
    public string UsenetStreamingPriority { get; set; } = string.Empty;

    [JsonPropertyName("usenet.article-buffer-size")]
    public string UsenetArticleBufferSize { get; set; } = string.Empty;

    [JsonPropertyName("webdav.user")]
    public string WebdavUser { get; set; } = string.Empty;

    [JsonPropertyName("webdav.show-hidden-files")]
    public string WebdavShowHiddenFiles { get; set; } = string.Empty;

    [JsonPropertyName("webdav.enforce-readonly")]
    public string WebdavEnforceReadonly { get; set; } = string.Empty;

    [JsonPropertyName("webdav.preview-par2-files")]
    public string WebdavPreviewPar2Files { get; set; } = string.Empty;

    [JsonPropertyName("rclone.mount-dir")]
    public string RcloneMountDir { get; set; } = string.Empty;

    [JsonPropertyName("media.library-dir")]
    public string MediaLibraryDir { get; set; } = string.Empty;

    [JsonPropertyName("repair.enable")]
    public string RepairEnable { get; set; } = string.Empty;

    [JsonPropertyName("cache.max-size-gb")]
    public string CacheMaxSizeGb { get; set; } = string.Empty;

    [JsonPropertyName("cache.max-age-hours")]
    public string CacheMaxAgeHours { get; set; } = string.Empty;

    [JsonPropertyName("cache.directory")]
    public string CacheDirectory { get; set; } = string.Empty;

    [JsonPropertyName("cache.precache-enable")]
    public string CachePrecacheEnable { get; set; } = string.Empty;

    [JsonPropertyName("cache.precache-max-file-size-mb")]
    public string CachePrecacheMaxFileSizeMb { get; set; } = string.Empty;

    [JsonPropertyName("cache.read-ahead-enable")]
    public string CacheReadAheadEnable { get; set; } = string.Empty;

    [JsonPropertyName("cache.read-ahead-segments")]
    public string CacheReadAheadSegments { get; set; } = string.Empty;

    [JsonPropertyName("cache.l2.enabled")]
    public string CacheL2Enabled { get; set; } = string.Empty;

    [JsonPropertyName("cache.l2.endpoint")]
    public string CacheL2Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("cache.l2.bucket-name")]
    public string CacheL2BucketName { get; set; } = string.Empty;

    [JsonPropertyName("cache.l2.access-key")]
    public string CacheL2AccessKey { get; set; } = string.Empty;

    [JsonPropertyName("cache.l2.secret-key")]
    public string CacheL2SecretKey { get; set; } = string.Empty;

    [JsonPropertyName("cache.l2.ssl")]
    public string CacheL2Ssl { get; set; } = string.Empty;

    [JsonPropertyName("cache.metadata-shared-enabled")]
    public string CacheMetadataSharedEnabled { get; set; } = string.Empty;

    [JsonPropertyName("cache.metadata-retention-days")]
    public string CacheMetadataRetentionDays { get; set; } = string.Empty;

    [JsonPropertyName("api.key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("webdav.pass")]
    public string WebdavPass { get; set; } = string.Empty;

    [JsonPropertyName("arr.instances")]
    public string ArrInstances { get; set; } = string.Empty;
}

public sealed class AdminSettingsHasSecrets
{
    [JsonPropertyName("api.key")]
    public bool ApiKey { get; set; }


    [JsonPropertyName("cache.l2.access-key")]
    public bool CacheL2AccessKey { get; set; }

    [JsonPropertyName("cache.l2.secret-key")]
    public bool CacheL2SecretKey { get; set; }

    [JsonPropertyName("webdav.pass")]
    public bool WebdavPass { get; set; }

    [JsonPropertyName("arr.instances")]
    public bool ArrInstances { get; set; }
}

public sealed class AdminSettingsRequest
{
    public Dictionary<string, string?> Config { get; init; } = new(StringComparer.Ordinal);
    public List<string> ClearSecrets { get; init; } = [];
}

public sealed class ArrSettingsDto
{
    public List<ArrInstanceDto> RadarrInstances { get; init; } = [];
    public List<ArrInstanceDto> SonarrInstances { get; init; } = [];
    public List<ArrQueueRuleDto> QueueRules { get; init; } = [];
}

public sealed class ArrInstanceDto
{
    public string Host { get; init; } = "";
    public bool HasApiKey { get; init; }
}

public sealed class ArrQueueRuleDto
{
    public string? Message { get; init; }
    public int Action { get; init; }
}

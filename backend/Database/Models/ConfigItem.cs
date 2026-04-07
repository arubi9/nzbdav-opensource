namespace NzbWebDAV.Database.Models;

public class ConfigItem
{
    public string ConfigName { get; set; } = null!;
    public string ConfigValue { get; set; } = null!;
    /// <summary>
    /// True when <see cref="ConfigValue"/> is stored as encrypted ciphertext at rest.
    /// False for plaintext rows and intentionally non-sensitive keys.
    /// </summary>
    public bool IsEncrypted { get; set; }
}

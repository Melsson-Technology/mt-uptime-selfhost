namespace MT.Uptime.Core.Settings;

public interface ISettingsService
{
    /// <summary>Returns the email settings with the API key decrypted (cached in memory).</summary>
    Task<EmailSettings> GetEmailAsync(CancellationToken ct = default);

    /// <summary>Persists email settings. A blank <see cref="EmailSettings.ApiKey"/> keeps the existing key.</summary>
    Task SaveEmailAsync(EmailSettings settings, CancellationToken ct = default);

    /// <summary>
    /// The effective retention configuration: the stored override if present, otherwise the
    /// <c>EngineOptions</c> default. Cached in memory.
    /// </summary>
    Task<RetentionSettings> GetRetentionAsync(CancellationToken ct = default);

    /// <summary>Persists the retention override (clamped to a sensible minimum) and invalidates the cache.</summary>
    Task SaveRetentionAsync(RetentionSettings settings, CancellationToken ct = default);
}

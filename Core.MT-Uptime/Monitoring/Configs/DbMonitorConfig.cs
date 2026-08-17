namespace MT.Uptime.Core.Monitoring.Configs;

/// <summary>
/// Shared config for MySQL and PostgreSQL monitors. The <see cref="Password"/> is stored
/// <b>encrypted</b> (Data Protection) and decrypted by the checker at connect time.
/// </summary>
public sealed class DbMonitorConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }

    /// <summary>Encrypted password ciphertext (never plaintext at rest).</summary>
    public string? Password { get; set; }
}

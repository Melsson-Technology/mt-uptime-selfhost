namespace MT.Uptime.Core.Settings;

/// <summary>Global SendGrid email configuration used for alert delivery.</summary>
public sealed class EmailSettings
{
    public string? ApiKey { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }

    /// <summary>Recipient of alert emails.</summary>
    public string? ToEmail { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(FromEmail) &&
        !string.IsNullOrWhiteSpace(ToEmail);
}

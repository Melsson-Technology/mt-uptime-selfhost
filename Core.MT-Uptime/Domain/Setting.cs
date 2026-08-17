namespace MT.Uptime.Core.Domain;

/// <summary>
/// A runtime-editable application setting (key/value). Values flagged <see cref="IsSecret"/>
/// (e.g. the SendGrid API key) are encrypted at rest via the Data Protection API.
/// </summary>
public class Setting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
}

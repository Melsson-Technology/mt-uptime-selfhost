namespace MT.Uptime.Core.Settings;

/// <summary>Runtime-editable data-retention configuration.</summary>
public sealed class RetentionSettings
{
    /// <summary>Days of raw heartbeat history to keep before pruning (minimum 1).</summary>
    public int RawDays { get; set; }
}

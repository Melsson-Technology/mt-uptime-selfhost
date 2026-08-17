namespace MT.Uptime.Core.Monitoring;

/// <summary>A pluggable check strategy for one <see cref="MonitorType"/>. Register one per type.</summary>
public interface IMonitorChecker
{
    MonitorType Type { get; }
    Task<CheckResult> CheckAsync(MonitorContext ctx, CancellationToken ct);
}

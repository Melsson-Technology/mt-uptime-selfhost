using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DnsClient;
using MT.Uptime.Core.Monitoring.Configs;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Resolves a DNS record (A/AAAA/CNAME/MX/TXT), optionally against a custom resolver, and
/// optionally asserts the result contains an expected value. Uses DnsClient.NET because
/// System.Net.Dns can't query record types beyond host addresses.
/// </summary>
public sealed class DnsChecker(ILookupClient defaultClient) : IMonitorChecker
{
    public MonitorType Type => MonitorType.Dns;

    public async Task<CheckResult> CheckAsync(MonitorContext ctx, CancellationToken ct)
    {
        var cfg = Deserialize(ctx.ConfigJson);
        if (string.IsNullOrWhiteSpace(cfg.Hostname))
            return CheckResult.Down("Hostname not configured");

        if (!Enum.TryParse<QueryType>(cfg.RecordType, ignoreCase: true, out var queryType))
            queryType = QueryType.A;

        var client = defaultClient;
        if (!string.IsNullOrWhiteSpace(cfg.Resolver) && IPAddress.TryParse(cfg.Resolver, out var ns))
            client = new LookupClient(ns);

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await client.QueryAsync(cfg.Hostname, queryType, cancellationToken: ct);
            sw.Stop();

            if (resp.HasError)
                return CheckResult.Down(resp.ErrorMessage, sw.Elapsed.TotalMilliseconds);

            var values = Extract(resp, queryType).ToList();
            if (values.Count == 0)
                return CheckResult.Down($"No {queryType} records", sw.Elapsed.TotalMilliseconds);

            if (!string.IsNullOrWhiteSpace(cfg.ExpectedValue) &&
                !values.Any(v => v.Contains(cfg.ExpectedValue, StringComparison.OrdinalIgnoreCase)))
                return CheckResult.Down(
                    $"Expected \"{cfg.ExpectedValue}\" not in: {string.Join(", ", values)}",
                    sw.Elapsed.TotalMilliseconds);

            return CheckResult.Up(sw.Elapsed.TotalMilliseconds, string.Join(", ", values.Take(3)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { sw.Stop(); return CheckResult.Down(ex.Message, sw.Elapsed.TotalMilliseconds); }
    }

    private static IEnumerable<string> Extract(IDnsQueryResponse resp, QueryType type) => type switch
    {
        QueryType.A => resp.Answers.ARecords().Select(r => r.Address.ToString()),
        QueryType.AAAA => resp.Answers.AaaaRecords().Select(r => r.Address.ToString()),
        QueryType.CNAME => resp.Answers.CnameRecords().Select(r => r.CanonicalName.Value),
        QueryType.MX => resp.Answers.MxRecords().Select(r => r.Exchange.Value),
        QueryType.TXT => resp.Answers.TxtRecords().SelectMany(r => r.Text),
        _ => resp.Answers.Select(r => r.ToString() ?? string.Empty),
    };

    private static DnsMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<DnsMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }
}

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
                    $"Expected \"{cfg.ExpectedValue}\" not in: {Summarise(values)}",
                    sw.Elapsed.TotalMilliseconds);

            return CheckResult.Up(sw.Elapsed.TotalMilliseconds, Summarise(values));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { sw.Stop(); return CheckResult.Down(ProbeFailure.Describe(ex), sw.Elapsed.TotalMilliseconds); }
    }

    /// <summary>How many answer records are named in a check message before the rest are counted.</summary>
    private const int MaxValuesShown = 5;

    /// <summary>
    /// Renders an answer set for a check message: the first few records, then a count.
    /// <para>
    /// The Up path always capped this at three records; the mismatch path did not, and joined the whole
    /// set. Answer data is supplied by whoever is authoritative for the monitored name, and DnsClient
    /// falls back to TCP when a response is truncated, so a reply can carry roughly 64 KB — around 250
    /// TXT strings. That is the wrong asymmetry: the uncapped branch was the one that fires when
    /// something is <em>wrong</em>, and the resulting message goes into every heartbeat row and the
    /// outbound alert body, where its size can push the payload past what Telegram or Discord accept.
    /// A zone owner could therefore suppress the alert about their own record changing.
    /// </para>
    /// <para>
    /// <see cref="CheckResult"/> truncates as a backstop; capping here keeps the message legible rather
    /// than merely bounded — five records and a count says more than 1 KB of run-on text.
    /// </para>
    /// </summary>
    private static string Summarise(IReadOnlyList<string> values)
    {
        var shown = string.Join(", ", values.Take(MaxValuesShown));
        return values.Count > MaxValuesShown
            ? $"{shown} (+{values.Count - MaxValuesShown} more)"
            : shown;
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

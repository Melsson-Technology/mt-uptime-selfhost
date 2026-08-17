using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Monitoring.Configs;

namespace MT.Uptime.Core.Incidents;

/// <summary>
/// Resolves a monitor to the piece of infrastructure it actually runs on, so that failures sharing that
/// infrastructure can be grouped into a single <see cref="Incident"/>.
/// <para>
/// <b>This is not tagging.</b> A tag answers "whose is this" and is assigned by a human; the correlation
/// key answers "what did this run on" and is inferred. Twenty client sites on one box carry twenty
/// different tags and one correlation key, which is exactly the case worth grouping.
/// </para>
/// <para>
/// The key prefers the resolved IP over the hostname, and that preference is the whole point: twenty
/// sites on one server have twenty distinct hostnames, so hostname-only correlation would group nothing.
/// The hostname is kept as a fallback for when resolution fails, which at least still groups several
/// monitors pointed at the same name (a web check, a TLS check and a TCP check on one host).
/// </para>
/// </summary>
public sealed class CorrelationKeyResolver(ILogger<CorrelationKeyResolver> log)
{
    /// <summary>How long a successful lookup is reused. Hosts move rarely; incidents open rarely.</summary>
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(10);

    /// <summary>Failures are cached briefly too, so a broken resolver cannot be retried on every event.</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Hard bound on a lookup. This runs on the single heartbeat-writer loop, so it must never wait on
    /// DNS indefinitely — a stalled writer would back up every monitor's history, not just this one's.
    /// Two seconds is generous for a resolver that is working and short enough to be invisible when it
    /// is not, and the result is cached either way.
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CacheEntry(string Key, DateTime ExpiresAt);

    /// <summary>
    /// The address lookup, swappable in tests. Returning an empty array means "did not resolve" and the
    /// caller falls back to the hostname.
    /// </summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> Lookup { get; init; } = DefaultLookupAsync;

    /// <summary>
    /// The host this monitor depends on, or null when there isn't one we can infer.
    /// <para>
    /// Push monitors are null because the target contacts us — we never learn where it runs. DNS monitors
    /// key on their <i>resolver</i> rather than the name being queried: the resolver is the shared
    /// infrastructure whose failure takes several DNS monitors down together, whereas the queried name is
    /// the thing under test. A DNS monitor on the system resolver has no key for the same reason a push
    /// monitor doesn't — the dependency is real but not identifiable from config.
    /// </para>
    /// </summary>
    public static string? ExtractHost(MonitorType type, string configJson)
    {
        try
        {
            switch (type)
            {
                case MonitorType.Http:
                    var http = JsonSerializer.Deserialize<HttpMonitorConfig>(configJson);
                    if (string.IsNullOrWhiteSpace(http?.Url)) return null;
                    return Uri.TryCreate(http.Url, UriKind.Absolute, out var uri) ? Normalize(uri.Host) : null;

                case MonitorType.Tcp:
                    return Normalize(JsonSerializer.Deserialize<TcpMonitorConfig>(configJson)?.Host);

                case MonitorType.MySql:
                case MonitorType.Postgres:
                    return Normalize(JsonSerializer.Deserialize<DbMonitorConfig>(configJson)?.Host);

                case MonitorType.Tls:
                    return Normalize(JsonSerializer.Deserialize<TlsMonitorConfig>(configJson)?.Host);

                case MonitorType.Dns:
                    return Normalize(JsonSerializer.Deserialize<DnsMonitorConfig>(configJson)?.Resolver);

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            // A malformed config is the checker's problem to report; here it just means "cannot correlate".
            return null;
        }
    }

    /// <summary>
    /// The correlation key for a monitor, or null when it cannot be correlated. Prefixed by kind
    /// (<c>ip:</c> / <c>host:</c>) so a hostname can never collide with an address literal.
    /// </summary>
    public async Task<string?> GetKeyAsync(MonitorType type, string configJson, CancellationToken ct = default)
    {
        var host = ExtractHost(type, configJson);
        if (host is null) return null;

        // An address literal is already the key — resolving it would be a round trip to learn nothing.
        if (IPAddress.TryParse(host, out var literal)) return $"ip:{literal}";

        if (_cache.TryGetValue(host, out var hit) && hit.ExpiresAt > DateTime.UtcNow)
            return hit.Key;

        var key = await ResolveAsync(host, ct);
        return key;
    }

    private async Task<string> ResolveAsync(string host, CancellationToken ct)
    {
        var fallback = $"host:{host}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LookupTimeout);

            var addresses = await Lookup(host, timeout.Token);
            if (addresses.Length == 0)
            {
                _cache[host] = new CacheEntry(fallback, DateTime.UtcNow + NegativeTtl);
                return fallback;
            }

            // Sort so a round-robin record set doesn't hand two monitors on the same host different keys.
            var pick = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                                .ThenBy(a => a.ToString(), StringComparer.Ordinal)
                                .First();

            var key = $"ip:{pick}";
            _cache[host] = new CacheEntry(key, DateTime.UtcNow + PositiveTtl);
            return key;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Losing correlation is not worth failing anything over: the incident still opens, it just
            // groups on the hostname instead of the address.
            log.LogDebug(ex, "Correlation lookup for '{Host}' failed; falling back to the hostname", host);
            _cache[host] = new CacheEntry(fallback, DateTime.UtcNow + NegativeTtl);
            return fallback;
        }
    }

    private static async Task<IPAddress[]> DefaultLookupAsync(string host, CancellationToken ct)
        => await Dns.GetHostAddressesAsync(host, ct);

    private static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var trimmed = host.Trim().TrimEnd('.');            // "example.com." and "example.com" are one host
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }
}

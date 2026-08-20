using System.Collections.Concurrent;

namespace MT.Uptime.Web.Security;

/// <summary>
/// A short-lived cache of the assembled public status page, keyed by slug.
/// <para>
/// <c>/status/{slug}</c> is anonymous by design and is the one surface deliberately pointed at the
/// internet. Building it costs one 30-day heartbeat aggregation <b>per monitor</b> — on a 20-monitor page
/// against a month of minute-resolution beats that is roughly 860,000 row reads and over a second of
/// SQLite, and the page carries a <c>&lt;meta http-equiv="refresh" content="60"&gt;</c> so every open tab
/// re-requests it. Nothing rate-limited it and nothing cached it, so an anonymous caller could turn a
/// public URL into sustained database load on the same process and file the monitoring engine uses.
/// </para>
/// <para>
/// The whole view is cached rather than the individual uptime figures, so one pass serves every caller
/// asking for that slug. The window is deliberately far shorter than the page's own 60-second refresh:
/// worst-case staleness goes from 60 s to 75 s, while a flood collapses to four database passes a minute
/// per slug however many requests arrive.
/// </para>
/// </summary>
public sealed class PublicStatusCache
{
    /// <summary>
    /// How long an assembled page is reused. Short enough that a real outage still reaches the page
    /// within the refresh interval a reader already experiences.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(object? Value, DateTime ExpiresAt);

    /// <summary>
    /// Returns the cached view for <paramref name="slug"/>, or builds one with <paramref name="build"/>.
    /// <para>
    /// A miss may build concurrently under load — deliberately, rather than holding a per-slug lock:
    /// the work is read-only, and a lock here would let a slow query block every caller of that page
    /// instead of just being repeated. The cache exists to bound *sustained* cost, not to serialise it.
    /// </para>
    /// <para>
    /// A null result (unknown or unpublished slug) is cached too, so requests for slugs that do not exist
    /// cannot be used to bypass this by never populating it.
    /// </para>
    /// </summary>
    public async Task<T?> GetOrBuildAsync<T>(string slug, Func<Task<T?>> build, DateTime utcNow)
        where T : class
    {
        if (_entries.TryGetValue(slug, out var hit) && hit.ExpiresAt > utcNow)
            return (T?)hit.Value;

        var built = await build();
        _entries[slug] = new Entry(built, utcNow + Lifetime);

        // Bound the dictionary: slugs come from the URL, so an attacker requesting a million distinct
        // ones would otherwise grow this without limit. Cheap sweep, and only when it has actually grown.
        if (_entries.Count > 512) Sweep(utcNow);

        return built;
    }

    private void Sweep(DateTime utcNow)
    {
        foreach (var (key, entry) in _entries)
            if (entry.ExpiresAt <= utcNow)
                _entries.TryRemove(key, out _);
    }
}

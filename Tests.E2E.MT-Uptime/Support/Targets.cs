using System.Globalization;

namespace MT.Uptime.Tests.E2E.Support;

/// <summary>
/// The target manifest written by <c>e2e/install-targets.sh</c> — ports, credentials, DNS records and
/// certificate expiry dates for every service the battery probes.
/// <para>
/// Read once, lazily, and never thrown from at type-initialisation time. That last property is
/// load-bearing: xUnit constructs <c>[Fact]</c> attributes during discovery, so a static initialiser
/// that threw when the manifest was absent would turn "every test skipped" into a discovery error on
/// any machine that is not an E2E box — which is most of them, including CI and every laptop.
/// </para>
/// </summary>
public static class Targets
{
    /// <summary>Where install-targets.sh writes the manifest. Overridable for a box that keeps it elsewhere.</summary>
    public const string DefaultPath = "/etc/mt-uptime-e2e/targets.env";

    /// <summary>Environment variable that overrides <see cref="DefaultPath"/>.</summary>
    public const string PathVariable = "MTU_E2E_MANIFEST";

    private static readonly Lazy<IReadOnlyDictionary<string, string>?> Loaded = new(Load);

    public static string ManifestPath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } p ? p : DefaultPath;

    /// <summary>True when a readable manifest exists. Never throws; the whole skip mechanism rests on it.</summary>
    public static bool Available => Loaded.Value is not null;

    /// <summary>The reason the battery cannot run here, for an attribute's Skip message.</summary>
    public static string SkipReason =>
        $"No E2E target manifest at {ManifestPath}. Run 'sudo ./e2e/install-targets.sh' on a prepared box, "
        + $"or set {PathVariable}.";

    /// <summary>
    /// A five-line parser, to the same contract install-targets.sh writes and the shell reads with
    /// <c>source</c>: <c>KEY=VALUE</c>, unquoted, <c>#</c> comments, blank lines ignored. Deliberately
    /// not a general dotenv implementation — no quoting, no escapes, no interpolation — because the
    /// installer's self-check round-trips the file through both this shape and the shell, and a parser
    /// that accepted more than the writer produced would let the two drift apart unnoticed.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? Load()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(ManifestPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }

            // A file that exists but carries nothing usable is treated as absent rather than as a
            // half-configured box: skipping is honest, and failing 200 tests on a truncated manifest
            // says nothing about the product.
            return values.Count > 0 ? values : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException)
        {
            // The manifest is 0640 root:<test user>. Wrong group is a real misconfiguration, but it is
            // the installer's problem to report, not something to crash test discovery over.
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> Values =>
        Loaded.Value ?? throw new InvalidOperationException(
            $"{SkipReason} A test body reached Targets without being skipped — its attribute should be "
            + "[E2EFact]/[E2ETheory], which skip when the manifest is missing.");

    /// <summary>A required value. Missing means the manifest is older than this test.</summary>
    public static string Str(string key) =>
        Values.TryGetValue(key, out var v) && v.Length > 0
            ? v
            : throw new InvalidOperationException(
                $"The E2E manifest at {ManifestPath} has no value for '{key}'. It was written by an older "
                + "install-targets.sh; re-run it to regenerate the manifest in full.");

    public static int Int(string key) =>
        int.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"Manifest key '{key}' is not an integer: '{Str(key)}'");

    /// <summary>An ISO-8601 instant, read back as UTC. Used for the certificate expiry assertions.</summary>
    public static DateTime Utc(string key) =>
        DateTime.TryParse(Str(key), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var v)
            ? v
            : throw new InvalidOperationException($"Manifest key '{key}' is not a timestamp: '{Str(key)}'");

    public static string? Optional(string key) => Values.GetValueOrDefault(key);

    // --- named accessors ---------------------------------------------------------------------------
    //
    // Spelled out rather than left as string literals at every call site. The manifest is generated by
    // a shell script, so a renamed key is a runtime failure in ~200 tests; naming them once means it is
    // one compile error instead.

    public static string Host => Str("E2E_HOST");
    public static string Keyword => Str("E2E_KEYWORD");
    public static string Helper => Str("E2E_HELPER");

    public static string HttpBaseUrl => Str("HTTP_BASE_URL");
    public static int HttpsValidPort => Int("HTTPS_VALID_PORT");
    public static int HttpsExpiringPort => Int("HTTPS_EXPIRING_PORT");
    public static int HttpsExpiredPort => Int("HTTPS_EXPIRED_PORT");
    public static int HttpsUntrustedPort => Int("HTTPS_UNTRUSTED_PORT");
    public static string BasicUser => Str("HTTP_BASIC_USER");
    public static string BasicPassword => Str("HTTP_BASIC_PASS");
    public static string BearerToken => Str("HTTP_BEARER_TOKEN");

    public static int TcpPort => Int("TCP_PORT");
    public static int TcpBlackholePort => Int("TCP_BLACKHOLE_PORT");
    public static int TcpRefusedPort => Int("TCP_REFUSED_PORT");

    public static string DnsResolver => Str("DNS_RESOLVER");
    public static string DnsAName => Str("DNS_A_NAME");
    public static string DnsAValue => Str("DNS_A_VALUE");
    public static string DnsAaaaValue => Str("DNS_AAAA_VALUE");
    public static string DnsCnameName => Str("DNS_CNAME_NAME");
    public static string DnsCnameValue => Str("DNS_CNAME_VALUE");
    public static string DnsMxName => Str("DNS_MX_NAME");
    public static string DnsMxValue => Str("DNS_MX_VALUE");
    public static string DnsTxtName => Str("DNS_TXT_NAME");
    public static string DnsTxtValue => Str("DNS_TXT_VALUE");
    public static string DnsNxdomainName => Str("DNS_NXDOMAIN_NAME");

    public static string MySqlHost => Str("MYSQL_HOST");
    public static int MySqlPort => Int("MYSQL_PORT");
    public static string MySqlDatabase => Str("MYSQL_DATABASE");
    public static string MySqlUser => Str("MYSQL_USER");
    public static string MySqlPassword => Str("MYSQL_PASSWORD");

    public static string PostgresHost => Str("POSTGRES_HOST");
    public static int PostgresPort => Int("POSTGRES_PORT");
    public static string PostgresDatabase => Str("POSTGRES_DATABASE");
    public static string PostgresUser => Str("POSTGRES_USER");
    public static string PostgresPassword => Str("POSTGRES_PASSWORD");

    public static string CaCert => Str("CA_CERT");
    public static DateTime TlsValidNotAfter => Utc("TLS_VALID_NOT_AFTER");
    public static DateTime TlsExpiringNotAfter => Utc("TLS_EXPIRING_NOT_AFTER");
    public static DateTime TlsExpiredNotAfter => Utc("TLS_EXPIRED_NOT_AFTER");

    /// <summary>The installed instance's origin, appended to the manifest by smoke.sh. Tier 3 only.</summary>
    public static string? BaseUrl => Optional("MTU_BASE_URL");
    public static string? AdminUser => Optional("MTU_ADMIN_USER");
    public static string? AdminPassword => Optional("MTU_ADMIN_PASSWORD");

    /// <summary>True when smoke.sh has run and recorded an admin account for the UI tier.</summary>
    public static bool UiReady =>
        Available && !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(AdminPassword);
}

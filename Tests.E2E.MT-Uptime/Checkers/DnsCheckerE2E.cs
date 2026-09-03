using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Checkers;

/// <summary>
/// <see cref="DnsChecker"/> against a real authoritative resolver — dnsmasq on 127.0.0.2, serving a
/// zone that exists nowhere else.
/// <para>
/// The third checker with no behavioural tests before this file, and the one where a fake would have
/// proved least: the interesting behaviour is all in how DnsClient renders an answer, and that is
/// exactly what a stub would have invented. Trailing dots on CNAME and MX values, an NXDOMAIN arriving
/// as <c>HasError</c> rather than as an empty answer, and a record type that exists for the name but
/// has no records — none of those are guessable.
/// </para>
/// </summary>
public class DnsCheckerE2E : IClassFixture<CheckerHost>
{
    private readonly IMonitorChecker _dns;

    public DnsCheckerE2E(CheckerHost host) => _dns = host.For(MonitorType.Dns);

    private Task<CheckResult> ProbeAsync(
        string hostname,
        string recordType = "A",
        string? resolver = null,
        string? expected = null,
        TimeSpan? cancelAfter = null)
        => Probe.RunAsync(_dns, Probe.Context(MonitorType.Dns, new DnsMonitorConfig
        {
            Hostname = hostname,
            RecordType = recordType,
            // Defaulting to OUR resolver, never the system one: the zone exists only in dnsmasq, and a
            // test that silently fell through to systemd-resolved would report NXDOMAIN and look like
            // a product bug. The system-resolver path is tested deliberately, once, below.
            Resolver = resolver ?? Targets.DnsResolver,
            ExpectedValue = expected,
        }), cancelAfter);

    [E2EFact]
    public async Task An_A_record_resolves_to_its_address()
    {
        var result = await ProbeAsync(Targets.DnsAName);

        Assert.Equal(CheckStatus.Up, result.Status);
        // The answer set lands in StatusCode, as it does for TCP — Message stays null on the Up path.
        Assert.Equal(Targets.DnsAValue, result.StatusCode);
        Assert.Null(result.Message);
    }

    [E2EFact]
    public async Task An_AAAA_record_resolves_to_its_address()
    {
        var result = await ProbeAsync(Targets.DnsAName, "AAAA");

        Assert.Equal(CheckStatus.Up, result.Status);
        // Compared case-insensitively: IPv6 text is the resolver library's rendering, and whether it
        // lower-cases hex digits is its business rather than a contract worth pinning.
        Assert.Equal(Targets.DnsAaaaValue, result.StatusCode, ignoreCase: true);
    }

    [E2EFact]
    public async Task A_CNAME_answer_carries_a_trailing_dot()
    {
        // The detail that will trip up the first operator who sets ExpectedValue. DnsClient renders a
        // canonical name in its wire form — fully qualified, trailing dot — so the answer is
        // "a.e2e.test." and not "a.e2e.test".
        var result = await ProbeAsync(Targets.DnsCnameName, "CNAME");

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal(Targets.DnsCnameValue, result.StatusCode);
        Assert.EndsWith(".", result.StatusCode, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task An_ExpectedValue_without_the_trailing_dot_still_matches()
    {
        // Because the comparison is Contains, not equality. This is the saving grace of the previous
        // test: an operator who types the name they know still gets a working monitor. Pinned so that
        // a future change to exact matching has to be a deliberate decision about existing monitors.
        var withoutDot = Targets.DnsCnameValue.TrimEnd('.');

        var result = await ProbeAsync(Targets.DnsCnameName, "CNAME", expected: withoutDot);

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task An_MX_answer_is_the_exchange_host()
    {
        // Note what is NOT here: the preference number. Extract() projects MxRecords to Exchange only,
        // so a monitor cannot assert on priority.
        var result = await ProbeAsync(Targets.DnsMxName, "MX");

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Equal(Targets.DnsMxValue, result.StatusCode);
        Assert.DoesNotContain(Targets.Str("DNS_MX_PREFERENCE"), result.StatusCode!, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task A_TXT_answer_is_its_text()
    {
        var result = await ProbeAsync(Targets.DnsTxtName, "TXT");

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.Contains(Targets.DnsTxtValue, result.StatusCode!, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task A_matching_ExpectedValue_is_Up()
    {
        var result = await ProbeAsync(Targets.DnsAName, expected: Targets.DnsAValue);

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_mismatched_ExpectedValue_is_Down_and_shows_what_was_returned()
    {
        // The message has to name both halves. "Expected value not found" without the actual answer
        // sends the operator to dig(1); with it, a record that changed to a new address is diagnosed
        // from the alert.
        var result = await ProbeAsync(Targets.DnsAName, expected: "203.0.113.99");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.Contains("203.0.113.99", result.Message, StringComparison.Ordinal);
        Assert.Contains(Targets.DnsAValue, result.Message, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task An_ExpectedValue_match_is_case_insensitive()
    {
        // OrdinalIgnoreCase in the checker. Matters for CNAME and MX far more than for an address:
        // DNS names are case-insensitive on the wire and a resolver may return whatever case the zone
        // file used.
        var result = await ProbeAsync(Targets.DnsCnameName, "CNAME",
            expected: Targets.DnsCnameValue.ToUpperInvariant());

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_name_that_does_not_exist_is_Down()
    {
        // NXDOMAIN arrives as resp.HasError with the resolver's own error message, not as an empty
        // answer set — so this takes a different branch from the "no records of that type" case below,
        // and the two produce differently-worded alerts.
        var result = await ProbeAsync(Targets.DnsNxdomainName);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [E2EFact]
    public async Task A_name_with_no_records_of_that_type_is_Down()
    {
        // The name resolves — it has an A record — but no MX. A successful response with an empty
        // answer section, which is the branch NXDOMAIN does not reach.
        var result = await ProbeAsync(Targets.DnsAName, "MX");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("No MX records", result.Message);
    }

    [E2EFact]
    public async Task An_unrecognised_record_type_silently_becomes_A()
    {
        // DOCUMENTS A FOOT-GUN. Enum.TryParse fails and the checker falls back to QueryType.A with no
        // warning anywhere, so a monitor configured for "SRV" — or for "AAA" instead of "AAAA" — is
        // quietly an A-record monitor that will happily report Up.
        //
        // Asserted as it behaves rather than as it should behave. The fix is a validation message in
        // the editor, not a change here; a checker that started failing on an unknown type would take
        // down every existing monitor with a typo in it.
        var bogus = await ProbeAsync(Targets.DnsAName, "NOT-A-REAL-TYPE");
        var explicitly = await ProbeAsync(Targets.DnsAName, "A");

        Assert.Equal(CheckStatus.Up, bogus.Status);
        Assert.Equal(explicitly.StatusCode, bogus.StatusCode);
    }

    [E2EFact]
    public async Task A_resolver_that_is_not_an_IP_address_falls_back_to_the_system_resolver()
    {
        // THE SHARPEST FOOT-GUN IN THIS CHECKER, and a candidate product finding.
        //
        // Resolver goes through IPAddress.TryParse, and on failure the checker uses the system
        // resolver instead — silently. So "8.8.8.8 " with a stray space, or a resolver given as a
        // hostname, or "127.0.0.2:53" with the port the field does not accept, all change which
        // nameserver is being monitored without a word in the log or the UI.
        //
        // Observable here because our zone exists ONLY in dnsmasq: the same query that succeeds
        // against 127.0.0.2 fails against whatever the box's own resolver is. On a monitor pointed at
        // a public name the difference would be invisible, which is exactly what makes it dangerous.
        var typo = await ProbeAsync(Targets.DnsAName, resolver: "127.0.0.2:53", cancelAfter: TimeSpan.FromSeconds(30));
        var correct = await ProbeAsync(Targets.DnsAName);

        Assert.Equal(CheckStatus.Up, correct.Status);
        Assert.Equal(CheckStatus.Down, typo.Status);
    }

    [E2ETheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unconfigured_monitor_says_so_instead_of_querying(string hostname)
    {
        var result = await ProbeAsync(hostname);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("Hostname not configured", result.Message);
        Assert.Null(result.ResponseTimeMs);
    }

    [E2EFact]
    public async Task Break_and_restore_moves_the_zone_Down_and_back()
    {
        var before = await ProbeAsync(Targets.DnsAName);
        Assert.Equal(CheckStatus.Up, before.Status);

        using (var broken = TargetControl.Break(Target.Dns))
        {
            // Generous cancellation: with the resolver stopped, DnsClient works through its own retry
            // schedule (5s per attempt, twice by default) before giving up, and that outlives any
            // sensible per-check timeout. Correction #8 in the plan — a shorter budget here would turn
            // a real Down into a cancellation and this test would assert the wrong thing.
            var during = await ProbeAsync(Targets.DnsAName, cancelAfter: TimeSpan.FromSeconds(45));
            Assert.Equal(CheckStatus.Down, during.Status);

            broken.RestoreNow();

            var after = await ProbeAsync(Targets.DnsAName);
            Assert.Equal(CheckStatus.Up, after.Status);
        }
    }
}

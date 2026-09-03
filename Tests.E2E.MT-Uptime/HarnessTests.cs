using System.Net;
using System.Text;
using System.Text.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E;

/// <summary>
/// Tests of the harness itself, not of the product.
/// <para>
/// They exist because the two things most likely to be wrong about this project are structural rather
/// than behavioural, and both are cheap to check and expensive to discover late: whether the skip
/// mechanism actually skips, and whether a SECOND assembly in this repository can boot
/// <c>WebApplicationFactory&lt;Program&gt;</c> at all. Everything in the checker, pipeline and UI
/// tiers rests on those two answers.
/// </para>
/// <para>
/// Unlike every other class here, most of these run with no manifest and no target services — which is
/// the point. They are the tests that tell you the harness is sound on a machine that is not an E2E box.
/// </para>
/// </summary>
public class HarnessTests : IClassFixture<E2EAppFactory>
{
    private readonly E2EAppFactory _app;

    public HarnessTests(E2EAppFactory app) => _app = app;

    [Fact]
    public void The_test_assembly_runs_at_all()
    {
        // Deliberately trivial. It distinguishes "the project does not build or discover" from "every
        // test skipped", which are the same silence from the outside.
        Assert.True(true);
    }

    [Fact]
    public void The_manifest_reader_never_throws_when_the_manifest_is_absent()
    {
        // Targets.Available is what every skip decision is made from, and it is consulted during
        // xUnit's DISCOVERY phase, inside an attribute constructor. If it could throw, a machine with
        // no manifest would report errors instead of skips — and "all skipped, none failed on a
        // developer machine" is one of the battery's acceptance criteria.
        var available = Targets.Available;
        Assert.False(string.IsNullOrWhiteSpace(Targets.ManifestPath));
        Assert.False(string.IsNullOrWhiteSpace(Targets.SkipReason));

        // Whichever way it went, the answer has to be self-consistent: a claim that targets are
        // available must survive actually reading a required key.
        if (available) Assert.False(string.IsNullOrWhiteSpace(Targets.Host));
    }

    [E2EFact]
    public void An_E2E_fact_only_runs_when_the_manifest_is_present()
    {
        // The other half of the previous test, and it cannot be written any other way: on a box
        // without a manifest this method must never execute. When it does execute, the manifest must
        // be readable — so a green run here proves the attribute gates on the same condition the
        // reader reports, rather than on something that merely correlates with it.
        Assert.True(Targets.Available);
        Assert.Equal("127.0.0.1", Targets.Host);
    }

    [Fact]
    public async Task The_real_application_boots_from_this_second_test_assembly()
    {
        // The single most valuable test in the file.
        //
        // WebApplicationFactory<Program> locates the application's content root by reflecting over the
        // entry assembly and its MSBuild-generated metadata. That already works from
        // Tests.MT-Uptime; whether it works from a SECOND test assembly — with its own output
        // directory, its own static web assets manifest, and a Program.cs that was recently refactored
        // to `await app.RunAsync()` behind an extension method — is a different question, and every
        // pipeline and UI test depends on the answer.
        //
        // /healthz is the right probe: it is mapped with .AllowAnonymous(), so a 200 means the whole
        // pipeline built and the endpoint map ran, without needing an account or a cookie.
        var client = await _app.EnsureStartedAsync();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task First_run_setup_redirects_until_an_admin_exists()
    {
        // Proves the factory's database is genuinely fresh and its own, rather than picking up a
        // developer's App_Data — which would make every later assertion about seeded monitors
        // meaningless. A brand-new instance funnels every page to /setup.
        var client = await _app.EnsureStartedAsync();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/setup", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public void Every_actively_probed_monitor_type_resolves_from_the_real_container()
    {
        // Tier 1 asks the container for checkers rather than constructing them, so if that resolution
        // is broken the entire checker matrix fails with one confusing error. This isolates it.
        //
        // Push is excluded because it is passive: nothing reaches out for a push monitor, so it has no
        // checker at all — the watchdog flags a ping that never arrived.
        using var host = new CheckerHost();

        var expected = Enum.GetValues<MonitorType>()
            .Where(t => t != MonitorType.Push)
            .OrderBy(t => t);

        Assert.Equal(expected, host.Checkers.Keys.OrderBy(t => t));
    }

    [Fact]
    public void The_secret_protector_override_beats_the_engines_own_registration()
    {
        // CheckerHost registers PassthroughProtector AFTER AddMonitoringEngine, relying on
        // last-registration-wins for constructor injection. That is a real property of
        // Microsoft.Extensions.DependencyInjection rather than a coincidence, but it is exactly the
        // kind of assumption that stops holding after a refactor — and if it stopped holding, the
        // checkers would silently be handed the real Data Protection protector and every
        // credential-carrying Tier 1 test would fail on an unreadable key ring rather than on
        // anything to do with the product.
        using var host = new CheckerHost();

        Assert.IsType<PassthroughProtector>(host.Protector);
        Assert.Equal("plaintext", host.Protector.Unprotect(host.Protector.Protect("plaintext")));
    }

    [Fact]
    public async Task The_webhook_sink_records_a_real_alert_payload()
    {
        // The sink is the only thing standing between "the product sent an alert" and every Tier 2
        // assertion about alerts, and it parses a wire format rather than a C# object — so if its
        // property names drift from WebhookNotificationChannel's, every notification test fails with a
        // timeout that says nothing about why.
        //
        // The JSON below is copied from that channel's payload construction, field for field, and that
        // is deliberate: it is a second, independent statement of the contract. When somebody renames
        // a field in the product, this test is what tells them the rename is a breaking change to
        // everyone's webhook consumer, rather than the change sailing through green.
        using var sink = new WebhookSink();

        var payload = """
            {
              "monitorId": 42,
              "monitor": "e2e-http",
              "kind": "Down",
              "status": "Down",
              "previousStatus": "Up",
              "message": "Unexpected status 503",
              "responseTimeMs": 12.5,
              "timestamp": "2026-09-03T11:22:33.4440000Z",
              "incident": { "id": 7, "monitorCount": 2, "correlated": true },
              "diagnostics": { "lastStatusCode": "503", "resolvedAddress": "127.0.0.1" }
            }
            """;

        using var client = new HttpClient();
        var response = await client.PostAsync(sink.Url, new StringContent(payload, Encoding.UTF8, "application/json"));

        // 200 matters on its own: WebhookNotificationChannel reports delivery success from
        // IsSuccessStatusCode, so a sink that answered anything else would make the product log a
        // failed delivery for a test that is about to assert the delivery arrived.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alert = await sink.WaitForAsync(42, "Down", TimeSpan.FromSeconds(10));

        Assert.Equal("e2e-http", alert.Monitor);
        Assert.Equal("Up", alert.PreviousStatus);
        Assert.Equal("Unexpected status 503", alert.Message);
        Assert.Equal(12.5, alert.ResponseTimeMs);
        Assert.Equal(7, alert.IncidentId);
        Assert.Equal(2, alert.MonitorCount);
        Assert.Equal("503", alert.LastStatusCode);
    }

    [Fact]
    public async Task The_webhook_sink_can_prove_a_negative()
    {
        // Maintenance suppression and the "no alert while Pending" assertion are both negatives, and a
        // negative that cannot fail is worse than no test. This checks both directions: nothing
        // arriving passes, and something arriving fails loudly rather than being ignored.
        using var sink = new WebhookSink();

        await sink.AssertNoneAsync(1, "Down", TimeSpan.FromMilliseconds(600));

        using var client = new HttpClient();
        await client.PostAsync(sink.Url, new StringContent(
            """{"monitorId":1,"monitor":"m","kind":"Down","status":"Down","previousStatus":"Up"}""",
            Encoding.UTF8, "application/json"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.AssertNoneAsync(1, "Down", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task The_webhook_sink_says_what_it_did_receive_when_the_wait_times_out()
    {
        // The failure message is the whole value of this class on a bad day. "Expected Down, got
        // nothing" sends you looking at the monitor; "expected Down, got Degraded" tells you the
        // answer. Asserting on the message text is unusual and justified: this text IS the feature.
        using var sink = new WebhookSink();

        using var client = new HttpClient();
        await client.PostAsync(sink.Url, new StringContent(
            """{"monitorId":9,"monitor":"m","kind":"Degraded","status":"Degraded","previousStatus":"Up"}""",
            Encoding.UTF8, "application/json"));

        var e = await Assert.ThrowsAsync<TimeoutException>(
            () => sink.WaitForAsync(9, "Down", TimeSpan.FromSeconds(2)));

        Assert.Contains("Degraded", e.Message, StringComparison.Ordinal);
        Assert.Contains("#9", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_webhook_sinks_never_share_a_port()
    {
        // Every pipeline test class builds its own sink, and the assembly runs serially — but classes
        // still overlap at construction and teardown. A fixed port would collide the first time that
        // happened, and the symptom would be an HttpListenerException in a constructor, blamed on
        // whichever class happened to lose.
        using var a = new WebhookSink();
        using var b = new WebhookSink();

        Assert.NotEqual(a.Port, b.Port);
        Assert.EndsWith("/", a.Url, StringComparison.Ordinal);   // HttpListener prefixes must
    }

    [Theory]
    [InlineData(typeof(HttpMonitorConfig))]
    [InlineData(typeof(TcpMonitorConfig))]
    [InlineData(typeof(DnsMonitorConfig))]
    [InlineData(typeof(DbMonitorConfig))]
    [InlineData(typeof(TlsMonitorConfig))]
    [InlineData(typeof(PushMonitorConfig))]
    public void Probe_serialises_config_exactly_as_the_checkers_read_it_back(Type configType)
    {
        // The assumption the entire checker tier rests on, and the one that would fail silently.
        //
        // Every checker deserialises with a bare JsonSerializer.Deserialize<T>(json) — default options,
        // which means PascalCase names, CASE-SENSITIVE matching, and enums as integers. If Probe.Json
        // ever wrote camelCase, or a naming policy were configured globally, every field would take its
        // default instead: Url empty, Port 0, Tls Preferred. The checkers would then answer "not
        // configured" or probe the wrong thing, and roughly a hundred tests would fail at once with no
        // hint that the cause was one serializer option.
        //
        // Round-tripping through the same defaults the checkers use is what makes that impossible.
        var config = Activator.CreateInstance(configType)!;
        var json = Probe.Json(config);

        var round = JsonSerializer.Deserialize(json, configType);

        Assert.NotNull(round);
        foreach (var property in configType.GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            Assert.Equal(property.GetValue(config), property.GetValue(round));

            // And the name is on the wire in the casing the reader requires. A camelCase document
            // deserialises to defaults without complaint, so "it round-tripped" alone is not enough:
            // it would also round-trip through a policy applied to both directions.
            Assert.Contains($"\"{property.Name}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_enum_reaches_a_checker_as_a_number()
    {
        // Called out separately because it is the one that reads as fine and is not. A DbMonitorConfig
        // written with "Tls": "VerifyFull" deserialises to Preferred — the default, and the WEAKEST
        // mode — so a test asserting that VerifyFull connects would pass while actually proving that
        // Preferred does. That is a test which cannot fail, in the exact place the product's own
        // documentation admits the default is the unsafe one.
        var json = Probe.Json(new DbMonitorConfig { Tls = DbTlsMode.VerifyFull });

        Assert.Contains($"\"Tls\":{(int)DbTlsMode.VerifyFull}", json, StringComparison.Ordinal);

        var round = JsonSerializer.Deserialize<DbMonitorConfig>(json);
        Assert.Equal(DbTlsMode.VerifyFull, round!.Tls);
    }

    [Fact]
    public async Task The_real_TcpChecker_reports_a_live_socket_in_StatusCode()
    {
        // Runs anywhere, with no manifest and no E2E box: a TcpListener on a loopback port is a real
        // enough target for the one thing worth checking early.
        //
        // It pins the plan's correction #7 against the actual checker. The plan predicted the address
        // would be in Message; CheckResult.Up(ms, statusCode) leaves Message null and puts it in
        // StatusCode. Tier 1 asserts that on the box, but a wrong prediction here would only surface
        // there — and only after an instance had been provisioned. This is the same assertion, an hour
        // earlier and on any machine.
        using var host = new CheckerHost();
        var checker = host.For(MonitorType.Tcp);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var result = await Probe.RunAsync(checker, Probe.Context(
                MonitorType.Tcp, new TcpMonitorConfig { Host = "127.0.0.1", Port = port }));

            Assert.Equal(CheckStatus.Up, result.Status);
            Assert.Equal($"127.0.0.1:{port}", result.StatusCode);
            Assert.Null(result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task The_real_TcpChecker_refuses_a_port_nothing_listens_on()
    {
        // The negative half, and the negative control for the test above: the same probe against a
        // port that was just released must NOT report Up. Without it, a checker that returned Up
        // unconditionally would pass the previous test.
        using var host = new CheckerHost();
        var checker = host.For(MonitorType.Tcp);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Probe.RunAsync(checker, Probe.Context(
            MonitorType.Tcp, new TcpMonitorConfig { Host = "127.0.0.1", Port = port }));

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
    }

    [Fact]
    public void Every_break_target_has_a_name_the_helper_actually_accepts()
    {
        // The Target enum is a promise about another program's command line. The helper matches its
        // argument against a closed `case`, and its sudoers rule enumerates the exact pairs — so a
        // member here with no counterpart there fails at runtime, under sudo, in the middle of a
        // scenario, with an exit code rather than a message.
        //
        // This list is transcribed from e2e/targets/mt-uptime-e2e-target's TARGETS_EACH plus `all`.
        var accepted = new[] { "http", "http-slow", "tcp", "dns", "mysql", "postgres", "all" };

        var names = Enum.GetValues<Target>().Select(TargetControl.CliName).ToArray();

        Assert.Equal(accepted.OrderBy(n => n, StringComparer.Ordinal),
                     names.OrderBy(n => n, StringComparer.Ordinal));
    }
}

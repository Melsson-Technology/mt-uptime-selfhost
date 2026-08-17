using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Tests;

/// <summary>
/// Webhook URLs are credentials — the token is in the path — and are stored encrypted for that reason.
/// The default IHttpClientFactory logging records the full request URI, which would write them into the
/// system log in plaintext and quietly undo the encryption. These tests assert the redacting logger
/// never emits the path, so the next person to touch the HTTP wiring finds out immediately.
/// </summary>
public class WebhookLoggingTests
{
    // Shaped like the real thing; the token segments are what must never appear in a log.
    private const string SlackUrl = "https://hooks.slack.com/services/T0AAAAAAAAA/B0BBBBBBBBB/zzzzSECRETzzzz";
    private const string TelegramUrl = "https://api.telegram.org/bot123456:AAHsecretTOKENvalue/sendMessage";

    [Theory]
    [InlineData(SlackUrl, "zzzzSECRETzzzz")]
    [InlineData(TelegramUrl, "AAHsecretTOKENvalue")]
    public void The_credential_never_reaches_the_log(string url, string secret)
    {
        var (logger, sink) = NewLogger();
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

        logger.LogRequestStart(request);
        logger.LogRequestStop(null, request, ok, TimeSpan.FromMilliseconds(120));
        logger.LogRequestFailed(null, request, null, new HttpRequestException("boom"), TimeSpan.FromMilliseconds(9));

        var all = string.Join("\n", sink);
        Assert.DoesNotContain(secret, all);
        Assert.DoesNotContain(url, all);
        Assert.DoesNotContain("/services/", all);
        Assert.DoesNotContain("/bot", all);
    }

    [Fact]
    public void The_host_status_and_timing_are_still_logged()
    {
        // Redaction must not cost observability: "Slack rejected it" and "we never reached Slack" have
        // to stay distinguishable, or a silently-undelivered alert is undiagnosable.
        var (logger, sink) = NewLogger();
        var request = new HttpRequestMessage(HttpMethod.Post, SlackUrl);
        using var forbidden = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);

        logger.LogRequestStop(null, request, forbidden, TimeSpan.FromMilliseconds(434));

        var all = string.Join("\n", sink);
        Assert.Contains("hooks.slack.com", all);
        Assert.Contains("403", all);
        Assert.Contains("434", all);
    }

    [Fact]
    public void A_rejected_webhook_is_logged_above_information()
    {
        // A wrong or revoked webhook returns 403/404 and is otherwise invisible — the notification just
        // never arrives. It must not be buried at Information alongside every successful delivery.
        var (logger, sink, levels) = NewLoggerWithLevels();
        var request = new HttpRequestMessage(HttpMethod.Post, SlackUrl);
        using var forbidden = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        using var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

        logger.LogRequestStop(null, request, ok, TimeSpan.Zero);
        logger.LogRequestStop(null, request, forbidden, TimeSpan.Zero);

        Assert.Equal(LogLevel.Information, levels[0]);
        Assert.Equal(LogLevel.Warning, levels[1]);
        Assert.DoesNotContain("zzzzSECRETzzzz", string.Join("\n", sink));
    }

    [Fact]
    public void A_transport_failure_does_not_leak_the_url_through_the_exception()
    {
        // Some transport exceptions carry the request URI in ToString(); the logger records Message only.
        var (logger, sink) = NewLogger();
        var request = new HttpRequestMessage(HttpMethod.Post, SlackUrl);
        var ex = new HttpRequestException($"Connection to {SlackUrl} was reset");

        logger.LogRequestFailed(null, request, null, ex, TimeSpan.FromMilliseconds(18));

        // The message itself embeds the URL, so this asserts the shape of the failure path: anything
        // interpolated from an exception is attacker- (or library-) controlled and must be treated as
        // capable of carrying the secret.
        Assert.Contains("hooks.slack.com", string.Join("\n", sink));
    }

    [Fact]
    public async Task The_notify_client_can_actually_be_built_from_the_container()
    {
        // Regression test for a real production failure. AddLogger<T> RESOLVES T from the container
        // rather than constructing it, so registering the logger on the client without registering the
        // type itself throws "No service for type ... has been registered" — and not at startup, where
        // it would be obvious, but at the first notification send. The app boots healthy and then
        // silently cannot alert, which is the worst possible failure mode for a monitoring tool.
        //
        // Testing the logger in isolation cannot catch this; only building the real graph can.
        await using var db = await TestDatabase.CreateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
        services.AddMonitoringEngine();

        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        var client = factory.CreateClient(WebhookChannelBase.HttpClientName);

        Assert.NotNull(client);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }

    [Theory]
    [InlineData(HttpChecker.ClientDefault)]
    [InlineData(HttpChecker.ClientNoRedirect)]
    [InlineData(HttpChecker.ClientInsecure)]
    public async Task Every_named_probe_client_can_be_built_too(string name)
    {
        // Same class of failure, same blast radius: a broken probe client means no monitoring at all.
        await using var db = await TestDatabase.CreateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
        services.AddMonitoringEngine();

        await using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        Assert.NotNull(client);
        Assert.Contains("MT-Uptime", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task Every_checker_can_be_resolved_from_the_container()
    {
        // Third instance of the same failure class, and the one with no other net: a checker that cannot
        // be constructed is a monitor type that silently never runs. The unit tests all call the
        // constructors directly, so only building the real graph catches a dependency added to a checker
        // but not registered — which is exactly what happened when HttpChecker took ISecretProtector.
        //
        // Data Protection is registered here because the host does it, not AddMonitoringEngine.
        await using var db = await TestDatabase.CreateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
        services.AddMonitoringEngine();

        await using var sp = services.BuildServiceProvider();
        var checkers = sp.GetServices<IMonitorChecker>().ToList();

        // One per actively-probed MonitorType, and no type served twice — a duplicate would mean one
        // silently wins. Push is excluded because it is passive: nothing reaches out for it, so it has
        // no checker at all; PushMonitorManager's watchdog flags a ping that never arrives.
        var expected = Enum.GetValues<MonitorType>().Where(t => t != MonitorType.Push).OrderBy(t => t);
        Assert.Equal(expected, checkers.Select(c => c.Type).OrderBy(t => t));
    }

    [Fact]
    public async Task Every_notification_channel_can_be_resolved_from_the_container()
    {
        // Same failure, other half of the product: ChannelPayloadTests constructs the channels directly,
        // so it stays green if a class exists but was never registered in AddMonitoringEngine. The
        // symptom then is a channel type you can select and save, which silently never sends —
        // discovered during an outage, which is the worst possible time.
        //
        // Every type is expected, Email included: SendGridNotificationChannel serves it. (IEmailSender
        // is a separate thing, for transactional mail like password resets, and is not a channel.)
        await using var db = await TestDatabase.CreateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
        services.AddMonitoringEngine();

        await using var sp = services.BuildServiceProvider();
        var channels = sp.GetServices<INotificationChannel>().ToList();

        Assert.Equal(
            Enum.GetValues<NotificationChannelType>().OrderBy(t => t),
            channels.Select(c => c.Type).OrderBy(t => t));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static (RedactingHttpClientLogger, List<string>) NewLogger()
    {
        var (logger, sink, _) = NewLoggerWithLevels();
        return (logger, sink);
    }

    private static (RedactingHttpClientLogger, List<string>, List<LogLevel>) NewLoggerWithLevels()
    {
        var sink = new List<string>();
        var levels = new List<LogLevel>();
        var logger = new RedactingHttpClientLogger(new CapturingLogger<RedactingHttpClientLogger>(sink, levels));
        return (logger, sink, levels);
    }

    private sealed class CapturingLogger<T>(List<string> sink, List<LogLevel> levels) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            levels.Add(level);
            // Capture the FORMATTED message — the same string a real provider writes to journald.
            sink.Add(formatter(state, ex) + (ex is null ? "" : " " + ex));
        }
    }
}

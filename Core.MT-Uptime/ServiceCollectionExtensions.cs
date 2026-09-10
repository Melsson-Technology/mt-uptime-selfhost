using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Core.Security;
using MT.Uptime.Core.Settings;

namespace MT.Uptime.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole monitoring engine: secret protection, settings, checkers, notification
    /// channels + dispatcher, the single-writer heartbeat pipeline, the scheduler, and the HTTP clients.
    /// The host must also register <c>AddDbContextFactory&lt;AppDbContext&gt;</c> and Data Protection.
    /// </summary>
    public static IServiceCollection AddMonitoringEngine(this IServiceCollection services)
    {
        services.AddOptions<EngineOptions>();

        // Shared infrastructure
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<MonitorStateService>();
        services.AddSingleton<MonitorStatsService>();
        services.AddSingleton<StatusPages.StatusPageService>();
        services.AddSingleton<Tags.TagService>();
        services.AddSingleton<Maintenance.MaintenanceWindowService>();
        services.AddSingleton<Incidents.CorrelationKeyResolver>();
        services.AddSingleton<Incidents.IncidentService>();
        services.AddSingleton<Incidents.AlertSuppressionService>();
        services.AddSingleton<Incidents.AlertEnricher>();

        // Checkers — one per MonitorType
        services.AddSingleton<IMonitorChecker, HttpChecker>();
        services.AddSingleton<IMonitorChecker, TcpChecker>();
        services.AddSingleton<IMonitorChecker, DnsChecker>();
        services.AddSingleton<IMonitorChecker, MySqlChecker>();
        services.AddSingleton<IMonitorChecker, PostgresChecker>();
        services.AddSingleton<IMonitorChecker, TlsChecker>();

        // Shared DNS resolver (thread-safe); DnsChecker makes a transient one only for custom resolvers.
        services.AddSingleton<DnsClient.ILookupClient>(new DnsClient.LookupClient());

        // Notification channels + dispatcher (hosted + injectable)
        services.AddSingleton<INotificationChannel, SendGridNotificationChannel>();
        services.AddSingleton<INotificationChannel, SlackNotificationChannel>();
        services.AddSingleton<INotificationChannel, WebhookNotificationChannel>();
        services.AddSingleton<INotificationChannel, TelegramNotificationChannel>();
        services.AddSingleton<INotificationChannel, DiscordNotificationChannel>();
        services.AddSingleton<INotificationChannel, TeamsNotificationChannel>();
        services.AddSingleton<INotificationChannel, NtfyNotificationChannel>();
        services.AddSingleton<INotificationChannel, GotifyNotificationChannel>();
        services.AddSingleton<INotificationChannel, PagerDutyNotificationChannel>();
        services.AddSingleton<NotificationChannelService>();
        // Transactional (non-alert) mail, e.g. password resets.
        services.AddSingleton<IEmailSender, EmailSender>();
        // RemoveAllLoggers is load-bearing, not tidying: the default logging writes the full request URI
        // at Information level, and for Slack/Telegram/webhook channels that URI is the credential. See
        // RedactingHttpClientLogger, which keeps host, status and timing but drops path and query.
        // AddLogger<T> resolves T from the container rather than constructing it, so it must be
        // registered or the client fails to build the first time a notification is sent.
        services.TryAddSingleton<RedactingHttpClientLogger>();
        services.AddHttpClient(WebhookChannelBase.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15))
            .RemoveAllLoggers()
            .AddLogger<RedactingHttpClientLogger>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());

        // Single-writer heartbeat pipeline + scheduler (each hosted + injectable)
        services.AddSingleton<HeartbeatWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<HeartbeatWriter>());
        services.AddSingleton<MonitorSchedulerService>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitorSchedulerService>());

        // Retention + stat rollups (hosted + injectable so the Settings page can trigger it on demand)
        services.AddSingleton<RetentionService>();
        services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());

        // Push / heartbeat monitors: the manager holds their state; the watchdog flags overdue pings.
        services.AddSingleton<PushMonitorManager>();
        services.AddHostedService<PushWatchdogService>();

        // Pooled HTTP clients for HttpChecker. Both per-monitor toggles live on the primary handler
        // rather than on a request, so the two independent axes are four registrations rather than two,
        // and the checker picks between them with HttpChecker.ClientNameFor — which walks the same four
        // cases. Taking the names from that one method is load-bearing rather than tidiness:
        // CreateClient with an unregistered name does not throw, it hands back a plain client that
        // follows redirects and validates certificates, so a name that drifted out of this list would
        // fail silently and in the least safe direction.
        // All four send our identifying User-Agent (see HttpChecker.UserAgent).
        AddProbeClient(services, ignoreTlsErrors: false, followRedirects: true);
        AddProbeClient(services, ignoreTlsErrors: false, followRedirects: false);
        AddProbeClient(services, ignoreTlsErrors: true, followRedirects: true);
        AddProbeClient(services, ignoreTlsErrors: true, followRedirects: false);

        return services;
    }

    /// <summary>
    /// Registers one HttpChecker probe client for a pair of per-monitor toggles.
    /// <para>
    /// The timing instrumentation is applied here, in the one place all four clients are built, for the
    /// same reason <see cref="HttpChecker.ClientNameFor"/> is a total mapping: the previous shape of that
    /// method silently turned redirect-following back on for one combination, and four hand-written
    /// registrations would let exactly that class of bug back in.
    /// </para>
    /// </summary>
    private static void AddProbeClient(IServiceCollection services, bool ignoreTlsErrors, bool followRedirects)
        => services.AddHttpClient(HttpChecker.ClientNameFor(ignoreTlsErrors, followRedirects), ConfigureMonitorClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = followRedirects,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = ignoreTlsErrors
                    ? new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    }
                    : new SslClientAuthenticationOptions(),

                // Splits connection setup into its parts so an alert can say where a slow probe spent
                // its time. Only invoked when a new connection is actually opened; a pooled one skips
                // straight past, which is itself reported (ProbeTimingBreakdown.ConnectionReused).
                ConnectCallback = TimedConnectAsync,
                PlaintextStreamFilter = (ctx, _) =>
                {
                    // Runs once the plaintext stream exists, i.e. after any TLS handshake. Measuring the
                    // handshake as the gap since the socket connected is the only handle we get on it.
                    ProbeTimings.Current?.RecordHandshakeComplete();
                    return ValueTask.FromResult(ctx.PlaintextStream);
                },
            });

    /// <summary>
    /// Opens a connection, timing the DNS lookup and the TCP handshake separately.
    /// <para>
    /// This replaces the default connect path for every HTTP monitor, so it deliberately mirrors what
    /// the default does — resolve, then try the returned addresses in order — rather than improving on
    /// it. <c>ConnectAsync</c> with the full address array preserves the fallback to a second address
    /// when the first refuses, which is what makes a dual-stack or multi-A-record host work.
    /// </para>
    /// </summary>
    private static async ValueTask<Stream> TimedConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var timings = ProbeTimings.Current;
        var sw = Stopwatch.StartNew();

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        }
        finally
        {
            // Recorded even when resolution throws. Without this a DNS failure leaves every leg null,
            // which is indistinguishable from a pooled connection — so ProbeTimingBreakdown.ConnectionReused
            // reported "connection reused" on the one probe where DNS is the fault, telling the operator the
            // network path was ruled out when nothing had been.
            timings?.RecordDns(sw.Elapsed);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            sw.Restart();
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            }
            finally
            {
                // Same reason, and this is the leg the feature most wants: "refused after 20 s in the TCP
                // connect" and "failed instantly at DNS" carry the same message and are completely
                // different faults. Recording only successful connects threw that distinction away.
                timings?.RecordConnect(sw.Elapsed);
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            // The stream never got built, so nothing downstream will dispose the socket for us.
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Applies the shared monitor User-Agent to a named HttpChecker client.</summary>
    private static void ConfigureMonitorClient(HttpClient client)
        => client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpChecker.UserAgent);
}

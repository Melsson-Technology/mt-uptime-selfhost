using System.Net.Security;
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

        // Pooled HTTP clients for HttpChecker; per-monitor toggles select a variant.
        // All three send our identifying User-Agent (see HttpChecker.UserAgent).
        services.AddHttpClient(HttpChecker.ClientDefault, ConfigureMonitorClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.AddHttpClient(HttpChecker.ClientNoRedirect, ConfigureMonitorClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.AddHttpClient(HttpChecker.ClientInsecure, ConfigureMonitorClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            });

        return services;
    }

    /// <summary>Applies the shared monitor User-Agent to a named HttpChecker client.</summary>
    private static void ConfigureMonitorClient(HttpClient client)
        => client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpChecker.UserAgent);
}

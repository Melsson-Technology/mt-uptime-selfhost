using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Settings;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Consumes state-transition events off a channel (decoupled from the check hot-path) and delivers each
/// to the global email baseline plus every enabled channel that is a default or linked to the monitor.
/// A slow or failing channel never stalls monitoring or the other channels.
/// <para>
/// Every event passes <see cref="AlertSuppressionService"/> first — acknowledgement, snooze and
/// maintenance windows all decide here, at dispatch, rather than anywhere upstream. Suppressing earlier
/// would mean not recording the outage; suppressing per-channel would mean deciding it nine times.
/// </para>
/// </summary>
public sealed class NotificationDispatcher(
    ISettingsService settings,
    IEnumerable<INotificationChannel> channels,
    NotificationChannelService channelService,
    AlertSuppressionService suppression,
    AlertEnricher enricher,
    ILogger<NotificationDispatcher> log) : BackgroundService
{
    private readonly Channel<NotificationEvent> _queue =
        Channel.CreateUnbounded<NotificationEvent>(new UnboundedChannelOptions { SingleReader = true });

    private IReadOnlyDictionary<NotificationChannelType, INotificationChannel> _impls =
        new Dictionary<NotificationChannelType, INotificationChannel>();

    public void Enqueue(NotificationEvent evt) => _queue.Writer.TryWrite(evt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _impls = channels.ToDictionary(c => c.Type);

        await foreach (var evt in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await DispatchAsync(evt, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Failed to dispatch notification for '{Monitor}'", evt.MonitorName); }
        }
    }

    private async Task DispatchAsync(NotificationEvent original, CancellationToken ct)
    {
        var decision = await suppression.EvaluateAsync(original, ct);
        if (decision.Suppress)
        {
            // Logged rather than dropped silently: "why did I not get paged" has to be answerable.
            log.LogInformation("Suppressed {Kind} alert for '{Monitor}' — {Reason}",
                original.Kind, original.MonitorName, decision.Reason);
            return;
        }

        // Enriched once here, not per channel: nine channels would otherwise repeat the same reads, and
        // the incident the suppression gate already found is handed straight over rather than re-queried.
        var evt = await enricher.EnrichAsync(original, decision.Incident, ct);

        // 1) Global email baseline (configured on the Settings page).
        if (_impls.TryGetValue(NotificationChannelType.Email, out var email))
        {
            var cfg = await settings.GetEmailAsync(ct);
            if (cfg.IsConfigured)
                await SafeSendAsync(email, evt, JsonSerializer.Serialize(cfg), "email", ct);
        }

        // 2) Default + per-monitor channels (Slack/webhook/Telegram).
        foreach (var ch in await channelService.GetChannelsForMonitorAsync(evt.MonitorId, ct))
        {
            if (_impls.TryGetValue(ch.Type, out var impl))
                await SafeSendAsync(impl, evt, ch.ConfigJson, ch.Name, ct);
        }
    }

    private async Task SafeSendAsync(INotificationChannel impl, NotificationEvent evt, string configJson, string label, CancellationToken ct)
    {
        try
        {
            if (!await impl.SendAsync(evt, configJson, ct))
                log.LogWarning("Notification channel '{Label}' reported failure for '{Monitor}'", label, evt.MonitorName);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Notification channel '{Label}' threw for '{Monitor}'", label, evt.MonitorName);
        }
    }
}

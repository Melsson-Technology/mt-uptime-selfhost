using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Pipeline;

/// <summary>
/// The whole application, running, with somewhere for its alerts to go.
/// <para>
/// Tier 2's unit of work is a running system: the scheduler starting a runner on its own interval, a
/// checker probing a real service, the state machine deciding what the result means, the heartbeat
/// writer and the notification dispatcher acting on that decision, and a webhook arriving. Tier 1
/// proves each checker's answer; this proves the answer travels.
/// </para>
/// <para>
/// <b>Nothing happens in the constructor.</b> xUnit builds a class fixture before it honours the
/// <c>Skip</c> on the tests inside, so a fixture that booted a host or bound a port here would do both
/// on every developer machine that has no manifest — and any failure would be reported as a
/// class-level error rather than as a skip, breaking the battery's acceptance criterion. Both
/// expensive things are behind <see cref="StartAsync"/>, which only a running test calls.
/// </para>
/// </summary>
public sealed class PipelineFixture : IDisposable
{
    private E2EAppFactory? _app;
    private WebhookSink? _sink;

    /// <summary>The running application. Only valid after <see cref="StartAsync"/>.</summary>
    public E2EAppFactory App => _app
        ?? throw new InvalidOperationException("call StartAsync() first — the fixture constructor does no work on purpose");

    /// <summary>Where this application's alerts are delivered. Only valid after <see cref="StartAsync"/>.</summary>
    public WebhookSink Sink => _sink
        ?? throw new InvalidOperationException("call StartAsync() first — the fixture constructor does no work on purpose");

    /// <summary>
    /// Boots the application, opens the sink, and wires the two together with a default webhook
    /// channel. Idempotent, so every test in a class can open with it.
    /// <para>
    /// The channel is <c>IsDefault</c>, which means it fires for every monitor without any per-monitor
    /// link — the shape almost every scenario wants. <see cref="NotificationsScenarios"/> seeds its own
    /// non-default channels to test the other half.
    /// </para>
    /// </summary>
    public async Task StartAsync()
    {
        if (_app is not null) return;

        var app = new E2EAppFactory();
        var sink = new WebhookSink();
        try
        {
            await app.EnsureStartedAsync();
            await app.SeedAdminAsync();
            await app.SeedWebhookChannelAsync(sink.Url);
        }
        catch
        {
            sink.Dispose();
            app.Dispose();
            throw;
        }

        _app = app;
        _sink = sink;
    }

    public void Dispose()
    {
        // The application first. Its scheduler is still probing, and a runner that lands a check
        // between the sink closing and the host stopping would log a delivery failure that belongs to
        // nothing — noise in the output of whichever test happened to be last.
        _app?.Dispose();
        _sink?.Dispose();
    }
}

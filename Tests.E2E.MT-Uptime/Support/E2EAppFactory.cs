using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Core.Security;
using MT.Uptime.Web.Services;

namespace MT.Uptime.Tests.E2E.Support;

/// <summary>
/// Boots the real application — the real pipeline, the real scheduler, the real notification
/// dispatcher — against a throwaway database and key ring, and points its monitors at the E2E target
/// services.
/// <para>
/// A copy of <c>Tests.MT-Uptime</c>'s <c>UptimeAppFactory</c>, not a reference to it. Two assemblies
/// each hosting <c>WebApplicationFactory&lt;Program&gt;</c> is fine; sharing one through a project
/// reference would drag the whole hermetic suite in as a dependency of the E2E project and give this
/// assembly a second set of test classes. If a third consumer ever appears, extract a Tests.Support
/// library rather than adding another copy.
/// </para>
/// <para>
/// <b>The constructor does no work.</b> xUnit constructs a class fixture before it honours any
/// <c>Skip</c> on the tests inside that class, so a fixture that booted the host or bound a port in
/// its constructor would do so even on a machine with no manifest — turning "all skipped" into a
/// class-level error. <see cref="WebApplicationFactory{T}"/> is lazy (the host starts on first
/// <c>Services</c> or <c>CreateClient</c> access), and nothing here forces it early. Call
/// <see cref="EnsureStartedAsync"/> from the test body instead.
/// </para>
/// </summary>
public sealed class E2EAppFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "e2e-admin";
    public const string AdminPassword = "e2e-seeded-password";
    public const string AdminEmail = "e2e-admin@example.test";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mt-uptime-e2e-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseSetting("Storage:DatabasePath", Path.Combine(_root, "e2e.db"));
        builder.UseSetting("Storage:DataProtectionKeysPath", Path.Combine(_root, "keys"));

        // Production, like the hermetic factory: Development would turn on the developer exception
        // page and change the middleware chain, and the battery exists to test what ships.
        builder.UseEnvironment(Environments.Production);

        // App:PublicBaseUrl is deliberately left unset. Declaring it narrows AllowedHosts, and the
        // resulting 400 on loopback requests is a documented trap that has already broken one real
        // deployment's health check.
    }

    /// <summary>
    /// Starts the host and returns a client. Idempotent, and the only place the host is forced to boot.
    /// <para>
    /// After this returns, <c>MonitorSchedulerService.ReloadAsync</c> is safe to call: its
    /// <c>_gate</c> is assigned synchronously before <c>ExecuteAsync</c>'s first <c>await</c>, and
    /// <c>IHost.StartAsync</c> awaits each hosted service's start — so the <c>if (_gate is null)
    /// return;</c> early-out cannot silently swallow a reload requested from here.
    /// </para>
    /// </summary>
    public async Task<HttpClient> EnsureStartedAsync()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // Touching Services is what guarantees the host is up rather than merely constructed.
        _ = Services.GetRequiredService<UserAccountService>();
        await Task.CompletedTask;
        return client;
    }

    /// <summary>Creates the admin account if absent, so the app is past first-run setup. Idempotent.</summary>
    public async Task SeedAdminAsync()
    {
        var users = Services.GetRequiredService<UserAccountService>();
        if (!await users.AnyUserExistsAsync())
            await users.CreateAsync(AdminUsername, AdminPassword, AdminEmail, UserRole.Admin);
    }

    public ISecretProtector Protector => Services.GetRequiredService<ISecretProtector>();

    public IDbContextFactory<AppDbContext> Db =>
        Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

    public MonitorSchedulerService Scheduler => Services.GetRequiredService<MonitorSchedulerService>();

    /// <summary>
    /// Inserts a monitor and activates it, returning its id.
    /// <para>
    /// Written straight to the database and then handed to the scheduler, which is exactly what the
    /// editor does (<c>MonitorEdit.razor</c> calls <c>ReloadAsync</c> after saving). The scheduler
    /// reads <c>Monitors</c> only once, at startup, so a row inserted without that call would never be
    /// probed — a monitor that silently never runs is the failure mode this method exists to avoid.
    /// </para>
    /// </summary>
    public async Task<int> SeedMonitorAsync(
        string name,
        MonitorType type,
        string configJson,
        int intervalSeconds = 5,
        int timeoutSeconds = 4,
        int retryCount = 0,
        int? slowThresholdMs = null,
        int degradedAfterChecks = 3,
        int resendEveryN = 0,
        bool upsideDown = false,
        bool enabled = true)
    {
        await using var db = await Db.CreateDbContextAsync();

        var monitor = new Monitor
        {
            Name = name,
            Type = type,
            ConfigJson = configJson,
            IntervalSeconds = intervalSeconds,
            TimeoutSeconds = timeoutSeconds,
            RetryCount = retryCount,
            SlowThresholdMs = slowThresholdMs,
            DegradedAfterChecks = degradedAfterChecks,
            ResendEveryN = resendEveryN,
            UpsideDown = upsideDown,
            Enabled = enabled,
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync();

        await Scheduler.ReloadAsync(monitor.Id);
        return monitor.Id;
    }

    /// <summary>
    /// Polls until a monitor reaches one of the wanted statuses, or the deadline passes.
    /// <para>
    /// Polling rather than a fixed delay, and the deadline has to be generous: a check can start up to
    /// one interval late (<c>MonitorRunner</c> jitters startup by up to <c>min(interval, 15s)</c>),
    /// takes up to the timeout, and a soft failure needs <c>RetryCount + 1</c> of them before Down is
    /// confirmed. Heartbeats themselves are written through an unbounded channel with no batching
    /// delay, so the database catches up in milliseconds once a decision is made.
    /// </para>
    /// </summary>
    public async Task<MonitorStatus> WaitForStatusAsync(
        int monitorId,
        MonitorStatus[] wanted,
        TimeSpan? within = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(60));
        MonitorStatus last = MonitorStatus.Pending;

        while (DateTime.UtcNow < deadline)
        {
            await using var db = await Db.CreateDbContextAsync(ct);
            var row = await db.Monitors.AsNoTracking()
                .Where(m => m.Id == monitorId)
                .Select(m => new { m.CurrentStatus })
                .FirstOrDefaultAsync(ct);

            if (row is not null)
            {
                last = row.CurrentStatus;
                if (wanted.Contains(last)) return last;
            }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException(
            $"Monitor {monitorId} was {last} after {(within ?? TimeSpan.FromSeconds(60)).TotalSeconds:0}s; "
            + $"expected one of {string.Join(", ", wanted)}.");
    }

    /// <summary>Every heartbeat for a monitor, oldest first — for asserting a Pending/Pending/Down shape.</summary>
    public async Task<List<Heartbeat>> HeartbeatsAsync(int monitorId, CancellationToken ct = default)
    {
        await using var db = await Db.CreateDbContextAsync(ct);
        return await db.Heartbeats.AsNoTracking()
            .Where(h => h.MonitorId == monitorId)
            .OrderBy(h => h.Timestamp).ThenBy(h => h.Id)
            .ToListAsync(ct);
    }

    /// <summary>The monitor row itself, for the fields only it carries — CertExpiresAt above all.</summary>
    public async Task<Monitor> MonitorAsync(int monitorId, CancellationToken ct = default)
    {
        await using var db = await Db.CreateDbContextAsync(ct);
        return await db.Monitors.AsNoTracking().FirstAsync(m => m.Id == monitorId, ct);
    }

    /// <summary>Every incident touching a monitor, newest first.</summary>
    public async Task<List<Incident>> IncidentsAsync(int monitorId, CancellationToken ct = default)
    {
        await using var db = await Db.CreateDbContextAsync(ct);

        // Through MonitorEvents rather than a MonitorId column, because an incident is not owned by one
        // monitor: correlation joins several monitors that failed on the same host into a single
        // incident, and that is precisely the case worth being able to assert on.
        var ids = await db.MonitorEvents.AsNoTracking()
            .Where(e => e.MonitorId == monitorId && e.IncidentId != null)
            .Select(e => e.IncidentId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .OrderByDescending(i => i.StartedAt).ThenByDescending(i => i.Id)
            .ToListAsync(ct);
    }

    /// <summary>Every state transition recorded for a monitor, oldest first.</summary>
    public async Task<List<MonitorEvent>> EventsAsync(int monitorId, CancellationToken ct = default)
    {
        await using var db = await Db.CreateDbContextAsync(ct);
        return await db.MonitorEvents.AsNoTracking()
            .Where(e => e.MonitorId == monitorId)
            .OrderBy(e => e.StartedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Seeds a webhook notification channel pointing at a test-hosted sink.
    /// <para>
    /// <b>The URL goes through the real <see cref="ISecretProtector"/>, and that is load-bearing.</b>
    /// <c>WebhookNotificationChannel</c> calls <c>Reveal</c> on it and returns false — no delivery, one
    /// warning in the log, no exception — when the value is not decryptable ciphertext. So a channel
    /// seeded with a plaintext URL produces a suite in which every notification assertion times out,
    /// and nothing anywhere says why. Unlike Tier 1, this factory runs the genuine Data Protection
    /// provider against a real key ring, so the ciphertext here is real.
    /// </para>
    /// </summary>
    public async Task<int> SeedWebhookChannelAsync(
        string url,
        bool isDefault = true,
        string name = "e2e-webhook",
        IReadOnlyList<int>? monitorIds = null,
        bool protect = true)
    {
        await using var db = await Db.CreateDbContextAsync();

        var channel = new NotificationChannel
        {
            Name = name,
            Type = NotificationChannelType.Webhook,
            Enabled = true,
            IsDefault = isDefault,
            ConfigJson = JsonSerializer.Serialize(new WebhookChannelConfig
            {
                Url = protect ? Protector.Protect(url) : url,
            }),
        };
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();

        foreach (var monitorId in monitorIds ?? [])
        {
            db.MonitorNotifications.Add(new MonitorNotification
            {
                MonitorId = monitorId,
                NotificationChannelId = channel.Id,
            });
        }
        if (monitorIds is { Count: > 0 }) await db.SaveChangesAsync();

        return channel.Id;
    }

    /// <summary>
    /// Polls until a monitor has at least <paramref name="count"/> heartbeats, then returns them.
    /// <para>
    /// For the shape assertions — Pending, Pending, Down — where waiting on the final status would
    /// race the writer: <c>Monitors.CurrentStatus</c> and the heartbeat rows are written by two
    /// independent paths, so a status of Down does not guarantee the beats behind it have landed.
    /// </para>
    /// </summary>
    public async Task<List<Heartbeat>> WaitForHeartbeatsAsync(
        int monitorId,
        int count,
        TimeSpan? within = null,
        CancellationToken ct = default)
    {
        var timeout = within ?? TimeSpan.FromSeconds(90);
        var deadline = DateTime.UtcNow + timeout;
        List<Heartbeat> beats;

        do
        {
            beats = await HeartbeatsAsync(monitorId, ct);
            if (beats.Count >= count) return beats;
            await Task.Delay(300, ct);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException(
            $"Monitor {monitorId} had {beats.Count} heartbeat(s) after {timeout.TotalSeconds:0}s, expected "
            + $"at least {count}: {string.Join(", ", beats.Select(b => $"{b.Status}(attempt {b.Attempt})"))}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}

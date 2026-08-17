using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;

namespace MT.Uptime.Core.Notifications;

/// <summary>CRUD for notification channels plus per-monitor link resolution.</summary>
public sealed class NotificationChannelService(IDbContextFactory<AppDbContext> factory)
{
    public async Task<List<NotificationChannel>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<NotificationChannel?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<int> SaveAsync(int? id, string name, NotificationChannelType type, string configJson,
        bool enabled, bool isDefault, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        NotificationChannel channel;
        if (id is int cid)
            channel = await db.NotificationChannels.FirstAsync(c => c.Id == cid, ct);
        else
        {
            channel = new NotificationChannel();
            db.NotificationChannels.Add(channel);
        }

        channel.Name = name;
        channel.Type = type;
        channel.ConfigJson = configJson;
        channel.Enabled = enabled;
        channel.IsDefault = isDefault;
        await db.SaveChangesAsync(ct);
        return channel.Id;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.NotificationChannels.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Channels that should fire for a monitor: enabled AND (marked default OR explicitly linked).</summary>
    public async Task<List<NotificationChannel>> GetChannelsForMonitorAsync(int monitorId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NotificationChannels.AsNoTracking()
            .Where(c => c.Enabled && (c.IsDefault || c.Monitors.Any(mn => mn.MonitorId == monitorId)))
            .ToListAsync(ct);
    }

    public async Task<List<int>> GetLinkedChannelIdsAsync(int monitorId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitorNotifications.AsNoTracking()
            .Where(mn => mn.MonitorId == monitorId)
            .Select(mn => mn.NotificationChannelId)
            .ToListAsync(ct);
    }

    /// <summary>Replace a monitor's explicit channel links with the given set.</summary>
    public async Task SetMonitorChannelsAsync(int monitorId, IReadOnlyList<int> channelIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.MonitorNotifications.Where(mn => mn.MonitorId == monitorId).ToListAsync(ct);

        db.MonitorNotifications.RemoveRange(existing.Where(mn => !channelIds.Contains(mn.NotificationChannelId)));
        var have = existing.Select(mn => mn.NotificationChannelId).ToHashSet();
        foreach (var cid in channelIds)
            if (!have.Contains(cid))
                db.MonitorNotifications.Add(new MonitorNotification { MonitorId = monitorId, NotificationChannelId = cid });

        await db.SaveChangesAsync(ct);
    }
}

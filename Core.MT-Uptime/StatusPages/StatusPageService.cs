using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;

namespace MT.Uptime.Core.StatusPages;

/// <summary>Public status-page reads (by slug) and admin CRUD, including monitor membership.</summary>
public sealed class StatusPageService(IDbContextFactory<AppDbContext> factory)
{
    /// <summary>Loads a published page with its ordered monitors, or null if missing/unpublished.</summary>
    public async Task<StatusPage?> GetPublishedBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.StatusPages.AsNoTracking()
            .Include(sp => sp.Monitors.OrderBy(m => m.SortOrder))
                .ThenInclude(spm => spm.Monitor)
            .FirstOrDefaultAsync(sp => sp.Slug == slug && sp.Published, ct);
    }

    public async Task<List<StatusPage>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.StatusPages.AsNoTracking().OrderBy(sp => sp.Title).ToListAsync(ct);
    }

    public async Task<(StatusPage Page, List<int> MonitorIds)?> GetForEditAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var page = await db.StatusPages.AsNoTracking()
            .Include(sp => sp.Monitors)
            .FirstOrDefaultAsync(sp => sp.Id == id, ct);
        if (page is null) return null;
        var ids = page.Monitors.OrderBy(m => m.SortOrder).Select(m => m.MonitorId).ToList();
        return (page, ids);
    }

    public async Task<int> SaveAsync(int? id, string slug, string title, string? description, bool published,
        IReadOnlyList<int> monitorIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        StatusPage page;
        if (id is int pid)
            page = await db.StatusPages.Include(p => p.Monitors).FirstAsync(p => p.Id == pid, ct);
        else
        {
            page = new StatusPage();
            db.StatusPages.Add(page);
        }

        page.Slug = slug;
        page.Title = title;
        page.Description = description;
        page.Published = published;

        // Diff-sync the join rows so re-saving the same monitors doesn't churn primary keys.
        foreach (var stale in page.Monitors.Where(spm => !monitorIds.Contains(spm.MonitorId)).ToList())
            page.Monitors.Remove(stale);

        for (var i = 0; i < monitorIds.Count; i++)
        {
            var mid = monitorIds[i];
            var existing = page.Monitors.FirstOrDefault(x => x.MonitorId == mid);
            if (existing is null)
                page.Monitors.Add(new StatusPageMonitor { MonitorId = mid, SortOrder = i });
            else
                existing.SortOrder = i;
        }

        await db.SaveChangesAsync(ct);
        return page.Id;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.StatusPages.Where(sp => sp.Id == id).ExecuteDeleteAsync(ct);
    }
}

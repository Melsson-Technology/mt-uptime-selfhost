using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;

namespace MT.Uptime.Core.Tags;

/// <summary>CRUD for tags, plus assignment to monitors.</summary>
public sealed class TagService(IDbContextFactory<AppDbContext> factory)
{
    public async Task<List<Tag>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>Tag ids and how many monitors carry each — for the dashboard's filter chips.</summary>
    public async Task<Dictionary<int, int>> CountsByTagAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitorTags.AsNoTracking()
            .GroupBy(mt => mt.TagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TagId, x => x.Count, ct);
    }

    /// <summary>Every monitor's tags in one query, keyed by monitor id. Avoids an N+1 on the dashboard.</summary>
    public async Task<Dictionary<int, List<Tag>>> TagsByMonitorAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.MonitorTags.AsNoTracking()
            .Include(mt => mt.Tag)
            .OrderBy(mt => mt.Tag!.Name)
            .Select(mt => new { mt.MonitorId, mt.Tag })
            .ToListAsync(ct);

        return rows
            .Where(r => r.Tag is not null)
            .GroupBy(r => r.MonitorId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Tag!).ToList());
    }

    public async Task<List<int>> GetTagIdsForMonitorAsync(int monitorId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitorTags.AsNoTracking()
            .Where(mt => mt.MonitorId == monitorId)
            .Select(mt => mt.TagId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a tag, or returns the existing one with that name. Returns null when the name is blank.
    /// <para>
    /// Get-or-create rather than create-or-fail because the editor lets you type a tag inline: a user
    /// typing "prod" on a second monitor means "the tag I already made", not an error. The unique index
    /// is NOCASE, so this also collapses "Prod" onto "prod" rather than tripping over it.
    /// </para>
    /// </summary>
    public async Task<Tag?> GetOrCreateAsync(string name, string? colour = null, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) return null;

        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct);
        if (existing is not null) return existing;

        var tag = new Tag
        {
            Name = name,
            Colour = NormaliseColour(colour),
            CreatedAt = DateTime.UtcNow,
        };
        db.Tags.Add(tag);

        try
        {
            await db.SaveChangesAsync(ct);
            return tag;
        }
        catch (DbUpdateException)
        {
            // Lost a race against another request creating the same name. The unique index did its job;
            // return what is there now rather than surfacing a conflict the caller cannot act on.
            await using var retry = await factory.CreateDbContextAsync(ct);
            return await retry.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.Name == name, ct);
        }
    }

    /// <summary>Renames and recolours a tag. Returns an error message, or null on success.</summary>
    public async Task<string?> UpdateAsync(int id, string name, string? colour, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) return "A tag needs a name.";

        await using var db = await factory.CreateDbContextAsync(ct);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag is null) return "Tag not found.";

        if (await db.Tags.AnyAsync(t => t.Id != id && t.Name == name, ct))
            return $"A tag called \"{name}\" already exists.";

        tag.Name = name;
        tag.Colour = NormaliseColour(colour);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Deletes a tag. Its assignments go with it (cascade) — a tag is a label, not an owner.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Tags.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Replaces a monitor's tags with the given set.</summary>
    public async Task SetMonitorTagsAsync(int monitorId, IReadOnlyList<int> tagIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.MonitorTags.Where(mt => mt.MonitorId == monitorId).ToListAsync(ct);

        db.MonitorTags.RemoveRange(existing.Where(mt => !tagIds.Contains(mt.TagId)));
        var have = existing.Select(mt => mt.TagId).ToHashSet();
        foreach (var tid in tagIds.Distinct())
            if (!have.Contains(tid))
                db.MonitorTags.Add(new MonitorTag { MonitorId = monitorId, TagId = tid });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Accepts <c>#RRGGBB</c> and falls back to the default for anything else. Validated rather than
    /// trusted because this value is interpolated into a <c>style</c> attribute on the dashboard.
    /// </summary>
    internal static string NormaliseColour(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour)) return Tag.DefaultColour;

        var v = colour.Trim();
        if (v.Length != 7 || v[0] != '#') return Tag.DefaultColour;

        for (var i = 1; i < v.Length; i++)
            if (!Uri.IsHexDigit(v[i]))
                return Tag.DefaultColour;

        return v.ToLowerInvariant();
    }
}

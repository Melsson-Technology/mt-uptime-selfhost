using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Tags;

namespace MT.Uptime.Tests;

/// <summary>
/// Tags against a real SQLite database. The interesting behaviour is not CRUD — it is the two things
/// that make a tag a shared object rather than a string on a row: name collision handling, and what
/// happens to assignments when either end is deleted.
/// </summary>
public class TagServiceTests
{
    [Fact]
    public async Task A_tag_is_created_once_and_reused_by_name()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);

        var first = await svc.GetOrCreateAsync("production", "#ff0000");
        var second = await svc.GetOrCreateAsync("production", "#00ff00");

        // Get-or-create, not create-or-fail: typing an existing tag on a second monitor means "that
        // one", and the colour of the existing tag wins rather than being silently overwritten.
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal("#ff0000", second.Colour);
        Assert.Single(await svc.ListAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("PRODUCTION")]
    [InlineData("  production  ")]
    public async Task Names_collide_case_insensitively_and_are_trimmed(string variant)
    {
        // Without NOCASE you get "prod" and "Prod" as separate tags, each matching half the monitors,
        // and a filter that silently under-reports. That is the failure this collation prevents.
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);

        var original = await svc.GetOrCreateAsync("production");
        var again = await svc.GetOrCreateAsync(variant);

        Assert.Equal(original!.Id, again!.Id);
        Assert.Single(await svc.ListAsync());
    }

    [Fact]
    public async Task A_blank_name_creates_nothing()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);

        Assert.Null(await svc.GetOrCreateAsync("   "));
        Assert.Empty(await svc.ListAsync());
    }

    [Fact]
    public async Task Renaming_onto_an_existing_name_is_refused()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);
        var a = await svc.GetOrCreateAsync("staging");
        await svc.GetOrCreateAsync("production");

        Assert.NotNull(await svc.UpdateAsync(a!.Id, "Production", null));   // case-insensitive collision
        Assert.Null(await svc.UpdateAsync(a.Id, "pre-production", "#123456"));

        var renamed = (await svc.ListAsync()).Single(t => t.Id == a.Id);
        Assert.Equal("pre-production", renamed.Name);
        Assert.Equal("#123456", renamed.Colour);
    }

    // --- Assignment ------------------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_monitors_tags_replaces_rather_than_accumulates()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);
        var monitorId = await db.SeedMonitorAsync();
        var prod = await svc.GetOrCreateAsync("production");
        var edge = await svc.GetOrCreateAsync("edge");
        var db2 = await svc.GetOrCreateAsync("database");

        await svc.SetMonitorTagsAsync(monitorId, [prod!.Id, edge!.Id]);
        await svc.SetMonitorTagsAsync(monitorId, [prod.Id, db2!.Id]);

        var assigned = await svc.GetTagIdsForMonitorAsync(monitorId);
        Assert.Equal(new[] { prod.Id, db2.Id }.OrderBy(x => x), assigned.OrderBy(x => x));
    }

    [Fact]
    public async Task Assigning_the_same_tag_twice_does_not_duplicate_it()
    {
        // The join has a composite primary key, so a duplicate would throw rather than double-insert —
        // which turns a harmless double-click into an error page.
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);
        var monitorId = await db.SeedMonitorAsync();
        var tag = await svc.GetOrCreateAsync("production");

        await svc.SetMonitorTagsAsync(monitorId, [tag!.Id, tag.Id]);

        Assert.Single(await svc.GetTagIdsForMonitorAsync(monitorId));
    }

    [Fact]
    public async Task Deleting_a_tag_unassigns_it_everywhere_and_leaves_the_monitor_alone()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);
        var monitorId = await db.SeedMonitorAsync();
        var tag = await svc.GetOrCreateAsync("production");
        await svc.SetMonitorTagsAsync(monitorId, [tag!.Id]);

        await svc.DeleteAsync(tag.Id);

        Assert.Empty(await svc.GetTagIdsForMonitorAsync(monitorId));

        // The monitor itself must survive — a tag is a label on a thing, not the thing.
        await using var ctx = db.CreateDbContext();
        Assert.NotNull(await ctx.Monitors.FindAsync(monitorId));
    }

    [Fact]
    public async Task Deleting_a_monitor_removes_its_assignments_but_keeps_the_tag()
    {
        // The other direction, and the one that would leave orphan join rows if the cascade were
        // configured on only one side.
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);
        var monitorId = await db.SeedMonitorAsync();
        var tag = await svc.GetOrCreateAsync("production");
        await svc.SetMonitorTagsAsync(monitorId, [tag!.Id]);

        await using (var ctx = db.CreateDbContext())
        {
            await ctx.Monitors.Where(m => m.Id == monitorId).ExecuteDeleteAsync();
        }

        await using var check = db.CreateDbContext();
        Assert.Empty(await check.MonitorTags.Where(mt => mt.MonitorId == monitorId).ToListAsync());
        Assert.Single(await svc.ListAsync());   // the tag is still available for other monitors
    }

    // --- Dashboard projections --------------------------------------------------------------------

    [Fact]
    public async Task Counts_and_lookups_cover_every_assignment()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);
        var a = await db.SeedMonitorAsync();
        var b = await db.SeedMonitorAsync();
        var prod = await svc.GetOrCreateAsync("production");
        var edge = await svc.GetOrCreateAsync("edge");

        await svc.SetMonitorTagsAsync(a, [prod!.Id, edge!.Id]);
        await svc.SetMonitorTagsAsync(b, [prod.Id]);

        var counts = await svc.CountsByTagAsync();
        Assert.Equal(2, counts[prod.Id]);
        Assert.Equal(1, counts[edge.Id]);

        var byMonitor = await svc.TagsByMonitorAsync();
        Assert.Equal(2, byMonitor[a].Count);
        Assert.Equal("production", byMonitor[b].Single().Name);
    }

    [Fact]
    public async Task An_untagged_monitor_is_absent_from_the_lookup_rather_than_present_and_empty()
    {
        // The dashboard's "Untagged" chip counts monitors missing from this dictionary, so an empty
        // list under the key would make that count read zero while untagged monitors exist.
        await using var db = await TestDatabase.CreateAsync();
        var svc = new TagService(db);
        var monitorId = await db.SeedMonitorAsync();

        Assert.False((await svc.TagsByMonitorAsync()).ContainsKey(monitorId));
    }

    // --- Colour ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#AABBCC", "#aabbcc")]
    [InlineData("  #123456  ", "#123456")]
    public void A_valid_hex_colour_is_kept(string input, string expected)
        => Assert.Equal(expected, TagService.NormaliseColour(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]                              // named colours are not hex
    [InlineData("#12345")]                           // too short
    [InlineData("#GGGGGG")]                          // not hex digits
    [InlineData("#000; background:url(x)")]          // the reason this is validated at all
    public void Anything_else_falls_back_to_the_default(string? input)
    {
        // The colour is interpolated into a style attribute on the dashboard, so it is validated rather
        // than trusted. Blazor encodes attribute values, but a value that reaches CSS should still be
        // known-good rather than merely escaped.
        Assert.Equal(Tag.DefaultColour, TagService.NormaliseColour(input));
    }
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Web.Security;

namespace MT.Uptime.Tests;

/// <summary>
/// A Viewer cannot change an incident, asserted at the service rather than through the UI.
/// <para>
/// The security review named this the largest untested load-bearing assumption in the application. The
/// <c>&lt;AuthorizeView&gt;</c> placement on <c>Incidents.razor</c> and <c>IncidentDetail.razor</c> is
/// correct, so this was never an exploitable finding — but an interactive Blazor page has no form post
/// to re-validate. It receives an event aimed at a handler id, and the entire Viewer-to-Editor boundary
/// on those two pages rested on a control that was never rendered having no id to aim at. That is a
/// property of the framework, and <c>SECURITY.md</c> promises this specific boundary to anyone reading
/// it. So the service now decides for itself, and these tests are what stop that decision being quietly
/// removed as "already covered by the page".
/// </para>
/// </summary>
public class IncidentAuthorizationTests
{
    private static IncidentService Incidents(TestDatabase tdb) =>
        new(tdb,
            new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance),
            Options.Create(new EngineOptions()),
            NullLogger<IncidentService>.Instance);

    /// <summary>
    /// An open incident, plus a real account to attribute actions to. The account is not incidental:
    /// AcknowledgedByUserId and PostedByUserId are foreign keys, so passing an id that does not exist
    /// fails on the FK rather than on authorization, and a positive control that cannot save proves
    /// nothing about who was allowed to.
    /// </summary>
    private static async Task<(TestDatabase Db, IncidentService Svc, long Id, int UserId)> OpenIncidentAsync()
    {
        var tdb = await TestDatabase.CreateAsync();
        var svc = Incidents(tdb);

        await using var db = tdb.CreateDbContext();
        var actor = new AppUser
        {
            Username = "operator",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            Role = UserRole.Editor,
        };
        db.Users.Add(actor);

        var incident = new Incident
        {
            Title = "db-primary is down",
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            Severity = MonitorStatus.Down,
        };
        db.Incidents.Add(incident);
        await db.SaveChangesAsync();
        return (tdb, svc, incident.Id, actor.Id);
    }

    public static TheoryData<UserRole> Refused => new() { UserRole.Viewer };
    public static TheoryData<UserRole> Allowed => new() { UserRole.Editor, UserRole.Admin };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_viewer_cannot_acknowledge_snooze_or_wake_an_incident(UserRole role)
    {
        var (tdb, svc, id, userId) = await OpenIncidentAsync();
        await using var _ = tdb;

        Assert.False(await svc.AcknowledgeAsync(id, role, userId: userId, DateTime.UtcNow));
        Assert.False(await svc.UnacknowledgeAsync(id, role));
        Assert.False(await svc.SnoozeAsync(id, role, TimeSpan.FromHours(1), DateTime.UtcNow));
        Assert.False(await svc.ClearSnoozeAsync(id, role));

        // Refused, not merely reported as refused.
        await using var db = tdb.CreateDbContext();
        var incident = await db.Incidents.SingleAsync(i => i.Id == id);
        Assert.Null(incident.AcknowledgedAt);
        Assert.Null(incident.SnoozedUntil);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_viewer_cannot_post_an_update_or_publish_an_incident(UserRole role)
    {
        var (tdb, svc, id, userId) = await OpenIncidentAsync();
        await using var _ = tdb;

        Assert.False(await svc.AddUpdateAsync(
            id, role, IncidentUpdateKind.Investigating, "Posted by someone who may not.", userId, DateTime.UtcNow));

        // Hiding, not publishing, is the act being refused. Incident.Published defaults to true on
        // purpose — a status page that stays green through an outage its own monitors are reporting is
        // worse than useless — so the deliberate act, and the one worth a role, is taking an incident
        // *off* the customer-facing page while it is still happening.
        Assert.False(await svc.SetPublishedAsync(id, role, published: false));

        await using var db = tdb.CreateDbContext();
        Assert.Empty(await db.IncidentUpdates.Where(u => u.IncidentId == id).ToListAsync());
        Assert.True((await db.Incidents.SingleAsync(i => i.Id == id)).Published);
    }

    // The positive controls. Without these the tests above would pass just as well against a service
    // that refused everybody, which pins nothing.

    [Theory]
    [MemberData(nameof(Allowed))]
    public async Task An_editor_or_admin_can_acknowledge_and_snooze(UserRole role)
    {
        var (tdb, svc, id, userId) = await OpenIncidentAsync();
        await using var _ = tdb;
        var now = DateTime.UtcNow;

        Assert.True(await svc.AcknowledgeAsync(id, role, userId: userId, now));
        Assert.True(await svc.SnoozeAsync(id, role, TimeSpan.FromHours(1), now));

        await using var db = tdb.CreateDbContext();
        var incident = await db.Incidents.SingleAsync(i => i.Id == id);
        Assert.NotNull(incident.AcknowledgedAt);
        Assert.NotNull(incident.SnoozedUntil);
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public async Task An_editor_or_admin_can_post_an_update_and_publish(UserRole role)
    {
        var (tdb, svc, id, userId) = await OpenIncidentAsync();
        await using var _ = tdb;

        Assert.True(await svc.AddUpdateAsync(
            id, role, IncidentUpdateKind.Investigating, "We are looking into it.", userId, DateTime.UtcNow));
        Assert.True(await svc.SetPublishedAsync(id, role, published: false));

        await using var db = tdb.CreateDbContext();
        Assert.Single(await db.IncidentUpdates.Where(u => u.IncidentId == id).ToListAsync());
        // Moved off the default, so this asserts the write happened rather than that it was already so.
        Assert.False((await db.Incidents.SingleAsync(i => i.Id == id)).Published);
    }

    // --- The claim the pages pass in ---------------------------------------------------------------
    //
    // The service is only as good as the role it is handed, and the pages read that from the principal.
    // PrincipalRole is where "no usable claim" becomes Viewer rather than something more generous.

    [Fact]
    public void An_anonymous_principal_is_a_viewer()
    {
        Assert.Equal(UserRole.Viewer, new ClaimsPrincipal(new ClaimsIdentity()).RoleOrViewer());
        Assert.Equal(UserRole.Viewer, ((ClaimsPrincipal?)null).RoleOrViewer());
    }

    [Theory]
    [InlineData("Admin", UserRole.Admin)]
    [InlineData("Editor", UserRole.Editor)]
    [InlineData("Viewer", UserRole.Viewer)]
    public void A_signed_in_principal_reports_the_role_in_its_claim(string claim, UserRole expected)
        => Assert.Equal(expected, Signed(claim).RoleOrViewer());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Administrator")]  // not a member
    [InlineData("admin")]          // right member, wrong case — not a value we would have written
    [InlineData("2")]              // the numeric value, which Enum.TryParse would otherwise accept
    [InlineData("SuperUser")]
    public void An_unrecognised_role_claim_falls_back_to_viewer(string claim)
        => Assert.Equal(UserRole.Viewer, Signed(claim).RoleOrViewer());

    /// <summary>
    /// "2" deserves its own note: <c>Enum.TryParse</c> happily parses the underlying number, so a claim
    /// of "2" would become Admin. Nothing writes that — the claim is <c>Role.ToString()</c> — which is
    /// exactly why it is worth pinning. A value nobody writes is a value nobody checks.
    /// </summary>
    [Fact]
    public void A_numeric_role_claim_does_not_become_an_administrator()
        => Assert.NotEqual(UserRole.Admin, Signed("2").RoleOrViewer());

    private static ClaimsPrincipal Signed(string role) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "someone"), new Claim(ClaimTypes.Role, role)],
            authenticationType: "Test"));
}

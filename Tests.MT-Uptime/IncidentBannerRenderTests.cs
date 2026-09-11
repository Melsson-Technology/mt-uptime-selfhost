using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;

namespace MT.Uptime.Tests;

/// <summary>
/// The incident banner on <c>MonitorDetail</c> reads as a sentence.
/// <para>
/// This exists because of a live defect that every other test was structurally incapable of seeing. The
/// banner is built from two correct strings — <c>&lt;strong&gt;Part of an open incident.&lt;/strong&gt;</c>
/// and one of two follow-on clauses — and Razor strips the newline and indent between an element and the
/// <c>@if</c> after it. Both halves were right, the assembly contained both, and the page said
/// <i>"Part of an open incident.No other monitor is affected."</i> to every operator who landed on it
/// during an outage. Nothing that asserts on strings can catch that: the bug is the absence of one
/// character <b>between</b> two things that are individually correct, and it shipped on 2026-09-09 and
/// again on 2026-09-10 without a single test going red.
/// </para>
/// <para>
/// So the assertion here is deliberately made against the text a person reads rather than against the
/// markup. <see cref="AsRendered"/> does the two things a browser does and nothing else — decode
/// character references, then collapse whitespace runs the way <c>white-space: normal</c> does. Asserting
/// on raw HTML would pass on the broken markup too, because the broken markup contains all the same
/// words; it is only after those two steps that the missing space becomes a different string.
/// </para>
/// <para>
/// The fix is an <c>&amp;#32;</c> entity rather than literal whitespace, precisely because literal
/// whitespace is what Razor removes. That is fragile knowledge living in one character, which is the
/// case for pinning it here. A Playwright test was written for this first and withdrawn: signing in per
/// test spent 18 of the login limiter's 20 permits per five minutes, so a nineteenth UI test failed
/// inside sign-in with a navigation timeout that looked nothing like a spent budget. This suite boots
/// the pipeline in-process and has no such ceiling.
/// </para>
/// </summary>
public class IncidentBannerRenderTests
{
    /// <summary>
    /// What a browser would display, and nothing else: markup carries no visible width, character
    /// references decode to characters, and runs of whitespace collapse to one space. The order matters
    /// — <c>&amp;#32;</c> has to become a real space <i>before</i> the collapse, or the collapse cannot
    /// see it and the test would measure the wrong thing.
    /// </summary>
    private static string AsRendered(string html)
    {
        var text = Regex.Replace(html, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Seeds a monitor sitting in an open incident and returns the rendered banner.
    /// <para>
    /// A Push monitor, because the type is irrelevant to the banner and a Push monitor is the one kind
    /// the scheduler will not go out and probe. The signed-in role is <see cref="UserRole.Viewer"/> for
    /// the same reason it is the weakest one available: the banner is information, not a control, and a
    /// test that only proved it renders for an Admin would not have covered the person most likely to be
    /// reading it.
    /// </para>
    /// </summary>
    private static async Task<string> RenderBannerAsync(int monitorCount)
    {
        await using var app = new UptimeAppFactory();

        int monitorId;
        await using (var db = await app.Services
            .GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var monitor = new Monitor
            {
                Name = "checkout-api",
                Type = MonitorType.Push,
                Enabled = true,
                CurrentStatus = MonitorStatus.Down,
                ConfigJson = JsonSerializer.Serialize(
                    new PushMonitorConfig { Token = PushMonitorManager.NewToken(), GraceSeconds = 30 }),
            };
            db.Monitors.Add(monitor);
            await db.SaveChangesAsync();
            monitorId = monitor.Id;

            var incident = new Incident
            {
                Title = "checkout-api is down",
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                LastEventAt = DateTime.UtcNow.AddMinutes(-5),
                Severity = MonitorStatus.Down,
                MonitorCount = monitorCount,
            };
            db.Incidents.Add(incident);
            await db.SaveChangesAsync();

            // MonitorDetail reads the newest event and shows the banner only while it is unresolved, so
            // an event with a null ResolvedAt is what makes the incident "open" from the page's side.
            db.MonitorEvents.Add(new MonitorEvent
            {
                MonitorId = monitorId,
                FromStatus = MonitorStatus.Up,
                ToStatus = MonitorStatus.Down,
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                ResolvedAt = null,
                IncidentId = incident.Id,
                Reason = "no ping within the grace period",
            });
            await db.SaveChangesAsync();
        }

        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "banner-reader", UserRole.Viewer);
        var response = await AuthFlow.GetAsync(client, $"/monitors/{monitorId}", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // Anchor on the class, then take the <span> inside it: the sentence is the span's text, and the
        // surrounding div also holds the "View incident" link, which is not part of it.
        var banner = html.IndexOf("incident-banner", StringComparison.Ordinal);
        Assert.True(banner >= 0, "the monitor is in an open incident and the page rendered no banner");

        var start = html.IndexOf("<span", banner, StringComparison.Ordinal);
        var end = html.IndexOf("</span>", banner, StringComparison.Ordinal);
        Assert.True(start > banner && end > start,
            "the banner is no longer a <span> inside .incident-banner — the markup moved and this test is now blind");

        return AsRendered(html[start..end]);
    }

    /// <summary>
    /// The single-monitor case, which is the one that was broken in production: the clause after the
    /// space is the <c>else</c> arm, and it is the arm an uncorrelated outage always takes.
    /// </summary>
    [Fact]
    public async Task A_single_monitor_incident_reads_as_a_sentence()
    {
        Assert.Equal(
            "Part of an open incident. No other monitor is affected.",
            await RenderBannerAsync(monitorCount: 1));
    }

    /// <summary>
    /// The correlated case, and the singular/plural switch inside it. Two members means <i>one</i> other
    /// monitor, which is the off-by-one the ternary exists to get right.
    /// </summary>
    [Theory]
    [InlineData(2, "1 other monitor is affected — this is not just this one.")]
    [InlineData(3, "2 other monitors are affected — this is not just this one.")]
    [InlineData(20, "19 other monitors are affected — this is not just this one.")]
    public async Task A_correlated_incident_names_the_others_and_agrees_in_number(
        int monitorCount, string expectedClause)
    {
        Assert.Equal(
            $"Part of an open incident. {expectedClause}",
            await RenderBannerAsync(monitorCount));
    }
}

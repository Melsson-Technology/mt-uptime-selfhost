using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace MT.Uptime.Tests.E2E.Ui;

/// <summary>
/// Locators for the shapes <c>GetByLabel</c> cannot address reliably.
/// <para>
/// <b>Every <c>&lt;select&gt;</c> in this application is wrapped in its label</b> — the house pattern
/// is <c>&lt;label&gt;Type &lt;InputSelect&gt;…&lt;/InputSelect&gt;&lt;/label&gt;</c> — which means the
/// label's text content is not "Type". It is
/// <c>"Type HTTP(S) TCP port DNS MySQL PostgreSQL SSL/TLS cert Heartbeat / cron (push)"</c>: every
/// option's text, concatenated. Measured against the running application, not inferred.
/// </para>
/// <para>
/// So <c>GetByLabel("Type", exact: true)</c> matches nothing, and dropping <c>exact</c> is worse
/// rather than better — a substring search for "Type" also hits "Content-Type" on the HTTP form and
/// "Record type" on the DNS one, giving a strict-mode violation on some monitor types and not others.
/// Reaching for the accessible name instead only trades one uncertainty for another, because the
/// accname of a control embedded in its own label is computed from the label text <em>plus the
/// control's value</em>.
/// </para>
/// <para>
/// This sidesteps all of it: find the label whose text <b>begins</b> with the field name, then the
/// select inside it. No accessible-name computation, no substring collisions, and it fails loudly
/// rather than silently picking the wrong control.
/// </para>
/// </summary>
public static class Forms
{
    /// <summary>
    /// Navigates to an <c>@rendermode InteractiveServer</c> page and waits for its circuit.
    /// <para>
    /// Blazor Server serves the first render over plain HTTP and then opens a WebSocket to
    /// <c>/_blazor</c>. Anything typed or clicked before that socket exists is applied to the DOM and
    /// never reaches the component — a filled field keeps its old value in the model, and Save
    /// persists what the operator did not type.
    /// </para>
    /// <para>
    /// The socket is the honest signal, and Playwright can watch for it. <c>window.Blazor</c> is not:
    /// the object exists as soon as the script loads, well before the circuit is connected. A fixed
    /// sleep is not either — it is only a guess that happens to be long enough on an idle box.
    /// </para>
    /// <para>
    /// The wait is armed BEFORE the navigation, because a socket opened while nobody was listening
    /// cannot be waited for afterwards.
    /// </para>
    /// </summary>
    public static async Task GotoInteractiveAsync(IPage page, string path)
    {
        var circuit = page.WaitForWebSocketAsync(new PageWaitForWebSocketOptions
        {
            Predicate = ws => ws.Url.Contains("/_blazor", StringComparison.Ordinal),
            Timeout = 30_000,
        });

        await page.GotoAsync(path);
        await circuit;
    }

    /// <summary>
    /// The control of the given kind inside the label whose text <b>begins</b> with
    /// <paramref name="labelText"/>.
    /// <para>
    /// This started as a select-only workaround and was generalised after the same defect appeared
    /// for an input: <c>&lt;label&gt;Ping URL &lt;span&gt;&lt;input readonly/&gt;&lt;button&gt;Copy
    /// &lt;/button&gt;&lt;/span&gt;&lt;/label&gt;</c> has the label text "Ping URL Copy", so
    /// <c>GetByLabel("Ping URL", exact: true)</c> matches nothing at all.
    /// </para>
    /// <para>
    /// The rule is simply: <c>GetByLabel</c> is safe only where the label wraps the control and
    /// nothing else. Any label containing a second element — a select's options, a button, a unit
    /// suffix — has text that is not the field name, and belongs here instead.
    /// </para>
    /// </summary>
    private static ILocator Labelled(IPage page, string labelText, string control) =>
        page.Locator("label")
            .Filter(new LocatorFilterOptions
            {
                // Anchored at the start and followed by a word boundary, so "Type" does not match
                // "Content-Type" (wrong position) and "Record type" does not match "Type" either.
                HasTextRegex = new Regex($@"^\s*{Regex.Escape(labelText)}\b"),
            })
            .Locator(control);

    /// <summary>The select belonging to the label that starts with <paramref name="labelText"/>.</summary>
    public static ILocator Select(IPage page, string labelText) => Labelled(page, labelText, "select");

    /// <summary>The input belonging to the label that starts with <paramref name="labelText"/>.</summary>
    public static ILocator Input(IPage page, string labelText) => Labelled(page, labelText, "input");

    /// <summary>Chooses <paramref name="value"/> in that select, by option value.</summary>
    public static Task SelectAsync(IPage page, string labelText, string value) =>
        Select(page, labelText).SelectOptionAsync(value);

    /// <summary>
    /// Chooses a value and waits for the re-render it is supposed to cause, retrying the choice if
    /// nothing happens.
    /// <para>
    /// <b>Every configuring page here is <c>@rendermode InteractiveServer</c>.</b> Blazor renders the
    /// page once over plain HTTP, then opens a WebSocket and re-renders through the circuit. In the
    /// gap between those two, a <c>select</c> is present and actionable — Playwright will happily
    /// operate it — but the change event reaches no component. The DOM's selected option moves and
    /// the application never learns of it.
    /// </para>
    /// <para>
    /// That is invisible until something depends on the re-render. On the first real run of this tier
    /// it took out seven tests: the monitor type select appeared to work, the type-specific fields
    /// never appeared, and <c>GetByLabel("Host")</c> waited thirty seconds for markup that was never
    /// going to be rendered. It failed for MySQL and passed for PostgreSQL in the same run, on
    /// identical markup, which is the signature of a race rather than a selector.
    /// </para>
    /// <para>
    /// Waiting for <c>window.Blazor</c> is necessary and not sufficient — the script object exists
    /// before the circuit is connected. So this asserts the EFFECT instead: choose, wait for the
    /// element that choice should bring into being, and if it does not arrive, choose again. A retry
    /// is honest here in a way it usually is not, because the failure being retried is "the
    /// application was not listening yet", which a second attempt genuinely fixes.
    /// </para>
    /// </summary>
    public static async Task SelectAndConfirmAsync(
        IPage page,
        string labelText,
        string value,
        ILocator appears,
        int attempts = 3)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await Select(page, labelText).SelectOptionAsync(value);

            try
            {
                await appears.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 5_000,
                });
                return;
            }
            catch (Exception) when (attempt < attempts)
            {
                // The circuit was not live. Give it a moment and choose again — re-selecting the same
                // value fires a fresh change event, which a connected circuit will act on.
                await page.WaitForTimeoutAsync(1_000);
            }
        }

        throw new TimeoutException(
            $"Choosing '{value}' for '{labelText}' never produced the expected re-render, after "
            + $"{attempts} attempts. The Blazor circuit is probably not connecting at all — check "
            + "that nginx is forwarding WebSocket upgrades.");
    }

    /// <summary>
    /// Clicks a control whose handler runs server-side, and confirms the navigation it should cause,
    /// retrying the click if nothing happens.
    /// <para>
    /// The same race as <see cref="SelectAndConfirmAsync"/>, in its other guise. A Blazor button is
    /// present in the static SSR markup, so Playwright finds it and clicks it happily — but until the
    /// circuit is connected that click reaches no component: no handler runs, no confirmation dialog
    /// appears, and nothing navigates. The symptom is a thirty-second wait for a navigation that was
    /// never going to happen.
    /// </para>
    /// <para>
    /// Found on the SECOND run of a box, in <c>DeleteMonitorAsync</c>. The first run won this race at
    /// every one of these call sites, which is the argument for running the battery twice: Tier 3
    /// drives the installed instance, whose database survives the run, so run two renders more rows
    /// and more history and connects its circuit later.
    /// </para>
    /// <para>
    /// Retrying is honest here for the reason it is in <see cref="SelectAndConfirmAsync"/>: the
    /// failure being retried is "the application was not listening yet", which a second click
    /// genuinely fixes. It is not a tolerance for a slow box — a connected circuit acts on the first
    /// click every time.
    /// </para>
    /// </summary>
    public static async Task ClickAndConfirmUrlAsync(
        IPage page,
        ILocator target,
        Func<string, bool> settled,
        string what,
        int attempts = 3,
        int perAttemptMs = 8_000)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // From the second attempt on: if the effect landed just after its own timeout expired,
            // stop rather than clicking a control that is no longer on the page. Deliberately NOT
            // checked before the first click, where a predicate that is already true would mean
            // returning having never clicked at all — and reporting that as success.
            if (attempt > 1 && settled(page.Url)) return;

            await target.ClickAsync();

            try
            {
                await page.WaitForURLAsync(settled, new PageWaitForURLOptions { Timeout = perAttemptMs });
                return;
            }
            catch (Exception) when (attempt < attempts)
            {
                // Not listening yet. Give the circuit a moment and click again.
                await page.WaitForTimeoutAsync(1_000);
            }
        }

        // Two causes, and the message must not pick one: an unconnected circuit looks exactly like a
        // page that stayed put on purpose.
        throw new TimeoutException(
            $"Clicking '{what}' never navigated away from {page.Url}, after {attempts} attempts. "
            + "Either the Blazor circuit is not connecting at all — check that nginx is forwarding "
            + "WebSocket upgrades — or the page stayed where it is deliberately, which for a form "
            + "means a validation error it is now displaying.");
    }

    /// <summary>
    /// Follows an in-app link to an <c>@rendermode InteractiveServer</c> page and waits for its
    /// circuit, by reading the link's href and navigating rather than by clicking it.
    /// <para>
    /// <see cref="GotoInteractiveAsync"/> can only arm its wait around a <c>GotoAsync</c>. A link
    /// CLICK lands on the destination with nothing armed, so anything typed on arrival races the
    /// circuit — which is how U11 could fill in an interval, save, and then find the old value still
    /// there, with the failure pointing at the editor rather than at the race.
    /// </para>
    /// <para>
    /// This one is deterministic rather than retried, because it can be: a full navigation always
    /// starts a fresh circuit, which is the property the other call sites here already rely on. The
    /// cost is that these sites no longer exercise Blazor's enhanced navigation. That is a real
    /// reduction in coverage, and it belongs in a test about enhanced navigation rather than living
    /// on as a race underneath tests about something else.
    /// </para>
    /// </summary>
    public static async Task FollowToInteractiveAsync(IPage page, ILocator link)
    {
        var href = await link.First.GetAttributeAsync("href");

        if (string.IsNullOrWhiteSpace(href))
        {
            throw new InvalidOperationException(
                "the link has no href to follow, so there is no navigation to arm a circuit wait "
                + "around. If this is a button rather than a link, use ClickAndConfirmUrlAsync.");
        }

        await GotoInteractiveAsync(page, href);
    }
}

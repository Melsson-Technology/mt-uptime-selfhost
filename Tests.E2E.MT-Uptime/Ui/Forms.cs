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
    /// <summary>The select belonging to the label that starts with <paramref name="labelText"/>.</summary>
    public static ILocator Select(IPage page, string labelText) =>
        page.Locator("label")
            .Filter(new LocatorFilterOptions
            {
                // Anchored at the start and followed by a word boundary, so "Type" does not match
                // "Content-Type" (wrong position) and "Record type" does not match "Type" either.
                HasTextRegex = new Regex($@"^\s*{Regex.Escape(labelText)}\b"),
            })
            .Locator("select");

    /// <summary>Chooses <paramref name="value"/> in that select, by option value.</summary>
    public static Task SelectAsync(IPage page, string labelText, string value) =>
        Select(page, labelText).SelectOptionAsync(value);
}

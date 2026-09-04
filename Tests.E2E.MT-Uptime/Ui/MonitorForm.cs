using Microsoft.Playwright;
using MT.Uptime.Core.Domain;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Ui;

/// <summary>
/// The monitor editor, driven the way an operator drives it.
/// <para>
/// One <c>Fill*</c> per monitor type, because <c>MonitorEdit.razor</c> renders a different set of
/// fields for each and the type select is what swaps them. Every locator here is
/// <c>GetByLabel</c> against the label text the product already renders — no test ids were added to
/// the product for this, and none should be: a selector that only a test uses is a selector that can
/// drift from what a person sees.
/// </para>
/// <para>
/// <b>Exact label matching throughout.</b> Playwright's default is a case-insensitive substring, and
/// this form has "Name", "Username", "Display name" and "Hostname" on it — so a non-exact
/// <c>GetByLabel("Name")</c> is ambiguous in a way that fails intermittently depending on which fields
/// the current type renders.
/// </para>
/// <para>
/// <b>Every <c>&lt;select&gt;</c> goes through <see cref="Forms"/> instead</b>, because a select
/// wrapped in its own label has a label text containing all of its option values — measured, not
/// assumed. See that class for why neither exact nor substring label matching can address them.
/// </para>
/// </summary>
public static class MonitorForm
{
    /// <summary>The battery's standard cadence: fast enough to watch, slow enough not to hammer.</summary>
    public const int IntervalSeconds = 5;
    public const int TimeoutSeconds = 4;

    /// <summary>
    /// Opens /monitors/new, chooses the type, names it, and sets the cadence.
    /// <para>
    /// <c>RetryCount</c> is set to 0 explicitly, and that is not redundant. The entity's default is 0
    /// but the <em>editor's</em> model defaults it to 1 (<c>MonitorEdit.razor</c>), so a monitor
    /// created through the UI needs two consecutive soft failures where a seeded one needs one. Every
    /// timing budget in this tier assumes the seeded behaviour, so the form is made to match.
    /// </para>
    /// </summary>
    public static async Task<IPage> BeginAsync(IPage page, MonitorType type, string name)
    {
        await Forms.GotoInteractiveAsync(page, "/monitors/new");

        // Choose the type and WAIT FOR THE FORM TO BECOME THAT TYPE. Selecting alone is not enough
        // before the Blazor circuit is live — see Forms.SelectAndConfirmAsync for what that costs.
        await Forms.SelectAndConfirmAsync(page, "Type", type.ToString(),
            page.GetByLabel(TypeAppearsAs(type), new() { Exact = true }));

        await page.GetByLabel("Name", new() { Exact = true }).FillAsync(name);

        // Push calls its cadence something else, because for a heartbeat monitor the interval is a
        // promise the monitored job makes rather than a rate we poll at.
        if (type == MonitorType.Push)
        {
            await page.GetByLabel("Expected period (s)", new() { Exact = true }).FillAsync(IntervalSeconds.ToString());
            await page.GetByLabel("Grace (s)", new() { Exact = true }).FillAsync("5");
        }
        else
        {
            await page.GetByLabel("Interval (s)", new() { Exact = true }).FillAsync(IntervalSeconds.ToString());
            await page.GetByLabel("Timeout (s)", new() { Exact = true }).FillAsync(TimeoutSeconds.ToString());
            await page.GetByLabel("Retries before down", new() { Exact = true }).FillAsync("0");
        }

        return page;
    }

    /// <summary>
    /// A field that exists on this monitor type's form and on no earlier-rendered one — the proof
    /// that choosing the type actually re-rendered the form rather than merely moving a select.
    /// <para>
    /// Http's is "URL" rather than something exotic because Http is the default: the form already
    /// shows it, so for that one type the wait is satisfied immediately and correctly.
    /// </para>
    /// </summary>
    private static string TypeAppearsAs(MonitorType type) => type switch
    {
        MonitorType.Http => "URL",
        MonitorType.Tcp => "Host",
        MonitorType.Dns => "Hostname",
        MonitorType.MySql => "Database",
        MonitorType.Postgres => "Database",
        MonitorType.Tls => "Warn when within (days)",
        MonitorType.Push => "Expected period (s)",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "no probe field for this type"),
    };

    /// <summary>Fills the type-specific half of the form and saves, returning the monitor's name.</summary>
    public static async Task CreateAsync(IPage page, MonitorType type, string name)
    {
        await BeginAsync(page, type, name);

        switch (type)
        {
            case MonitorType.Http:
                await page.GetByLabel("URL", new() { Exact = true }).FillAsync($"{Targets.HttpBaseUrl}/ok");
                break;

            case MonitorType.Tcp:
                await page.GetByLabel("Host", new() { Exact = true }).FillAsync(Targets.Host);
                await page.GetByLabel("Port", new() { Exact = true }).FillAsync(Targets.TcpPort.ToString());
                break;

            case MonitorType.Dns:
                await page.GetByLabel("Hostname", new() { Exact = true }).FillAsync(Targets.DnsAName);
                await Forms.SelectAsync(page, "Record type", "A");
                await page.GetByLabel("Resolver (optional)", new() { Exact = true }).FillAsync(Targets.DnsResolver);
                break;

            case MonitorType.MySql:
                await page.GetByLabel("Host", new() { Exact = true }).FillAsync(Targets.MySqlHost);
                await page.GetByLabel("Port", new() { Exact = true }).FillAsync(Targets.MySqlPort.ToString());
                await page.GetByLabel("Database", new() { Exact = true }).FillAsync(Targets.MySqlDatabase);
                await page.GetByLabel("Username", new() { Exact = true }).FillAsync(Targets.MySqlUser);
                await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Targets.MySqlPassword);
                break;

            case MonitorType.Postgres:
                await page.GetByLabel("Host", new() { Exact = true }).FillAsync(Targets.PostgresHost);
                await page.GetByLabel("Port", new() { Exact = true }).FillAsync(Targets.PostgresPort.ToString());
                await page.GetByLabel("Database", new() { Exact = true }).FillAsync(Targets.PostgresDatabase);
                await page.GetByLabel("Username", new() { Exact = true }).FillAsync(Targets.PostgresUser);
                await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Targets.PostgresPassword);
                break;

            case MonitorType.Tls:
                await page.GetByLabel("Host", new() { Exact = true }).FillAsync(Targets.Host);
                await page.GetByLabel("Port", new() { Exact = true }).FillAsync(Targets.HttpsValidPort.ToString());
                await page.GetByLabel("Warn when within (days)", new() { Exact = true }).FillAsync("14");
                break;

            case MonitorType.Push:
                // Nothing to fill: the editor generates the token itself and shows the ping URL
                // read-only. Reading that URL back is how PushPingUrlAsync finds it.
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "no form filler for this monitor type");
        }

        await SaveAsync(page);
    }

    public static async Task SaveAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        // The editor navigates back to the dashboard on a successful save, so waiting for the URL to
        // change is what distinguishes "saved" from "the form re-rendered with a validation error" —
        // and the latter would otherwise surface much later, as a monitor that mysteriously does not
        // exist.
        await page.WaitForURLAsync(u => !u.Contains("/monitors/new", StringComparison.Ordinal)
                                     && !u.Contains("/edit", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ping URL a push monitor was given, read out of the read-only field on the form.
    /// <para>
    /// The token exists nowhere else the test can reach: it is generated in the editor and stored
    /// encrypted-adjacent inside the monitor's config. Reading it off the page is not a shortcut, it is
    /// the same thing the operator has to do.
    /// </para>
    /// </summary>
    public static Task<string> PushPingUrlAsync(IPage page) =>
        // Forms.Input, not GetByLabel. This label wraps the input AND a "Copy" button, so its text
        // content is "Ping URL Copy" and an exact label match finds nothing — the same wrapping-label
        // problem as the selects, which is why Forms now handles both.
        Forms.Input(page, "Ping URL").InputValueAsync();

    /// <summary>
    /// Waits for a monitor's row on the dashboard to show a status.
    /// <para>
    /// The dashboard is an interactive circuit that pushes updates, so this does NOT reload — which is
    /// exactly what <c>LiveDashboardUiTests</c> is asserting, and what makes a polling loop with
    /// <c>ReloadAsync</c> the wrong tool even where it would work.
    /// </para>
    /// </summary>
    public static async Task WaitForStatusAsync(IPage page, string name, string statusText, int timeoutMs = 45_000)
    {
        var cell = page.Locator("tr", new() { HasText = name }).Locator("td[data-label='Status']");
        await Assertions.Expect(cell).ToHaveTextAsync(statusText, new() { Timeout = timeoutMs });
    }
}

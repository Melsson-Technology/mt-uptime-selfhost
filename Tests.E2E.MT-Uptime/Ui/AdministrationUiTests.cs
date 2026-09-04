using System.Text.Json;
using Microsoft.Playwright;
using MT.Uptime.Core.Domain;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Ui;

/// <summary>
/// U6, U7, U8, U9 and U12 — the status page, tags, users and roles, settings, and the admin exports.
/// <para>
/// The two here that carry real weight are U8 and U12, and both for the same reason: they are the only
/// tests in the battery that assert a <b>negative</b> about access. A role that grants too much and an
/// export that leaks a stored credential are both invisible from a screen that looks correct.
/// </para>
/// </summary>
public class AdministrationUiTests : IClassFixture<UiFixture>
{
    private readonly UiFixture _fx;

    public AdministrationUiTests(UiFixture fx) => _fx = fx;

    [UIFact]
    public async Task U6_a_published_status_page_is_visible_to_an_anonymous_visitor()
    {
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();

        var monitorName = $"u6-{Guid.NewGuid():N}"[..14];
        await MonitorForm.CreateAsync(page, MonitorType.Tcp, monitorName);
        await MonitorForm.WaitForStatusAsync(page, monitorName, "Up");

        var slug = $"u6-{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        await Forms.GotoInteractiveAsync(page, "/status-pages/new");
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("E2E status");
        await page.GetByLabel("Slug", new() { Exact = true }).FillAsync(slug);
        await page.GetByLabel("Published (visible at /status/<slug>)").CheckAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = monitorName }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/status-pages/new", StringComparison.Ordinal));

        // Fetched with a plain HttpClient carrying no cookie, which is the whole assertion: a status
        // page an operator has to be signed in to read is not a status page. Playwright's context
        // holds the auth cookie, so using the browser here would prove nothing.
        using var anonymous = new HttpClient { BaseAddress = new Uri(_fx.BaseUrl) };
        var response = await anonymous.GetAsync($"/status/{slug}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(monitorName, html, StringComparison.Ordinal);
        Assert.Contains("E2E status", html, StringComparison.Ordinal);

        await DeleteStatusPageAsync(page, slug);
        await MonitorsUiTests.DeleteMonitorAsync(page, monitorName);
    }

    [UIFact]
    public async Task U7_a_tag_filters_the_dashboard()
    {
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();

        var tag = $"u7tag{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        var tagged = $"u7-tagged-{Guid.NewGuid():N}"[..18];
        var untagged = $"u7-plain-{Guid.NewGuid():N}"[..18];

        // The tag is added from inside the monitor editor, which is where an operator meets it.
        await MonitorForm.BeginAsync(page, MonitorType.Tcp, tagged);
        await page.GetByLabel("Host", new() { Exact = true }).FillAsync(Targets.Host);
        await page.GetByLabel("Port", new() { Exact = true }).FillAsync(Targets.TcpPort.ToString());
        await page.GetByLabel("Add a tag", new() { Exact = true }).FillAsync(tag);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
        await MonitorForm.SaveAsync(page);

        await MonitorForm.CreateAsync(page, MonitorType.Tcp, untagged);

        await Forms.GotoInteractiveAsync(page, "/");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = tagged })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = untagged })).ToBeVisibleAsync();

        // Filtering is a client-side circuit interaction — no navigation — so both halves of the
        // assertion are about what the same rendered page shows after a click.
        await page.GetByRole(AriaRole.Button, new() { Name = tag }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = tagged })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = untagged })).ToHaveCountAsync(0);

        await MonitorsUiTests.DeleteMonitorAsync(page, tagged);
        await MonitorsUiTests.DeleteMonitorAsync(page, untagged);
    }

    [UIFact]
    public async Task U8_roles_are_enforced_for_an_Editor_and_a_Viewer()
    {
        // The only test in the battery that asserts somebody CANNOT do something, and the reason it is
        // worth the setup: RBAC is enforced by an [Authorize] attribute on each component, which is a
        // per-page opt-in. A page added without one is reachable by everybody, and nothing about the
        // page looks wrong.
        await _fx.StartAsync();
        var admin = await _fx.SignInAsync();

        var editor = $"u8ed{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        var viewer = $"u8vw{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        const string password = "e2e-role-password-123";

        await CreateUserAsync(admin, editor, "Editor", password);
        await CreateUserAsync(admin, viewer, "Viewer", password);

        // An Editor may configure monitors and may not manage users.
        var editorPage = await _fx.SignInAsync(editor, password);
        await Forms.GotoInteractiveAsync(editorPage, "/monitors/new");
        await Assertions.Expect(Forms.Select(editorPage, "Type")).ToBeVisibleAsync();

        // Plain GotoAsync, NOT GotoInteractiveAsync. A page the user is refused never starts a Blazor
        // circuit, so waiting for one can only ever time out — and it did, turning a correct denial
        // into a thirty-second failure that read like the navigation had broken.
        await editorPage.GotoAsync("/users");
        await AssertDeniedAsync(editorPage);

        // A Viewer may look and may not configure.
        var viewerPage = await _fx.SignInAsync(viewer, password);
        await Forms.GotoInteractiveAsync(viewerPage, "/");
        await Assertions.Expect(viewerPage.Locator("body")).ToBeVisibleAsync();

        // Plain GotoAsync for the same reason as above: a refused page has no circuit to wait for.
        await viewerPage.GotoAsync("/monitors/new");
        await AssertDeniedAsync(viewerPage);

        await DeleteUserAsync(admin, editor);
        await DeleteUserAsync(admin, viewer);
    }

    [UIFact]
    public async Task U9_a_settings_change_survives_a_reload()
    {
        // Deliberately trivial, and deliberately includes the reload. A Blazor form that updates its
        // own model without persisting looks identical to one that saved — until the next page load,
        // which in practice means until somebody notices retention never changed.
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();

        await Forms.GotoInteractiveAsync(page, "/settings");

        // Settings holds two independent EditForms — email and retention — each with its own "Save".
        // The Save is located by the form that CONTAINS the retention field rather than by index: an
        // Nth(1) here would silently start pressing the email form's Save the day a third section is
        // added above it, and the test would still pass by reloading a value it never changed.
        const string retentionLabel = "Keep raw heartbeat history (days)";
        var retentionForm = page.Locator("form", new() { Has = page.GetByLabel(retentionLabel, new() { Exact = true }) });
        var field = page.GetByLabel(retentionLabel, new() { Exact = true });

        var original = await field.InputValueAsync();
        var changed = original == "45" ? "60" : "45";

        await field.FillAsync(changed);
        await retentionForm.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        await page.ReloadAsync();
        await Assertions.Expect(page.GetByLabel(retentionLabel, new() { Exact = true })).ToHaveValueAsync(changed);

        // Put it back, because this tier writes to the installed instance's real settings and the
        // next run inherits whatever this one leaves behind.
        await page.GetByLabel(retentionLabel, new() { Exact = true }).FillAsync(original);
        await page.Locator("form", new() { Has = page.GetByLabel(retentionLabel, new() { Exact = true }) })
            .GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel(retentionLabel, new() { Exact = true })).ToHaveValueAsync(original);
    }

    [UIFact]
    public async Task U12_the_monitor_export_carries_no_secrets()
    {
        // A SECURITY ASSERTION, and the sharpest one in the battery.
        //
        // The export exists so an operator can move their monitors between instances, which means it
        // is downloaded, emailed, and committed to git. Every credential in it must be nulled — and
        // "must be nulled" is the sort of guarantee that survives a refactor only if something checks.
        //
        // A monitor carrying every kind of secret the product stores is created first, so this cannot
        // pass by exporting nothing.
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();

        var name = $"u12-{Guid.NewGuid():N}"[..14];
        const string password = "not-in-the-export-please";

        await MonitorForm.BeginAsync(page, MonitorType.MySql, name);
        await page.GetByLabel("Host", new() { Exact = true }).FillAsync(Targets.MySqlHost);
        await page.GetByLabel("Port", new() { Exact = true }).FillAsync(Targets.MySqlPort.ToString());
        await page.GetByLabel("Database", new() { Exact = true }).FillAsync(Targets.MySqlDatabase);
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync(Targets.MySqlUser);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await MonitorForm.SaveAsync(page);

        // Fetched through the browser's own session rather than a fresh client, because /admin/export
        // requires authentication and the cookie is what proves it.
        var response = await page.APIRequest.GetAsync($"{_fx.BaseUrl}/admin/export/monitors");
        Assert.True(response.Ok, $"the export answered {response.Status}");
        var json = await response.TextAsync();

        Assert.Contains(name, json, StringComparison.Ordinal);

        // The plaintext must not be there — but neither must the CIPHERTEXT, which is the part that is
        // easy to get wrong and feels safe. An export carrying encrypted secrets is still an export
        // that becomes readable to anyone who also has the key ring, which is anyone who has the
        // backup sitting beside it.
        Assert.DoesNotContain(password, json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var exported = doc.RootElement.EnumerateArray()
            .First(m => m.GetProperty("Name").GetString() == name);

        // Config is a nested object holding the type-specific settings, with the credential fields
        // nulled by AdminEndpoints.Redact. Asserting the field is PRESENT AND NULL rather than merely
        // absent: a redaction that dropped the key entirely would also pass a "not present" check, and
        // would silently change the shape every importer has to read.
        var config = exported.GetProperty("Config");
        Assert.Equal(JsonValueKind.Object, config.ValueKind);

        var passwordField = Assert.Contains("Password", config.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));
        Assert.Equal(JsonValueKind.Null, passwordField.ValueKind);

        // The rest of the named secret fields, wherever this config type carries them. Listed here as
        // a second, independent statement of AdminEndpoints.SecretConfigFields — a field added there
        // and not here is a gap, and a field added to a config and to neither is the leak that list
        // exists to prevent.
        foreach (var secretField in new[] { "AuthSecret", "Headers", "Token" })
        {
            if (config.TryGetProperty(secretField, out var value))
            {
                Assert.True(
                    value.ValueKind is JsonValueKind.Null,
                    $"the export carried a value for '{secretField}'; every stored credential must be nulled");
            }
        }

        var backup = await page.APIRequest.GetAsync($"{_fx.BaseUrl}/admin/backup");
        Assert.True(backup.Ok, $"the backup answered {backup.Status}");
        var head = (await backup.BodyAsync())[..15];
        Assert.Equal("SQLite format 3", System.Text.Encoding.ASCII.GetString(head));

        await MonitorsUiTests.DeleteMonitorAsync(page, name);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static async Task AssertDeniedAsync(IPage page)
    {
        // Denial can arrive two ways depending on how the page opted in — a redirect to /denied, or
        // the access-denied component rendered in place. Both are correct; what must not happen is the
        // page rendering its own content.
        var url = page.Url;
        var body = await page.Locator("body").InnerTextAsync();

        var denied = url.Contains("/denied", StringComparison.OrdinalIgnoreCase)
                     || url.Contains("/login", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("denied", StringComparison.OrdinalIgnoreCase)
                     || body.Contains("permission", StringComparison.OrdinalIgnoreCase);

        Assert.True(denied, $"expected access to be refused; landed on {url} showing:\n{body[..Math.Min(400, body.Length)]}");
    }

    private static async Task CreateUserAsync(IPage page, string username, string role, string password)
    {
        await Forms.GotoInteractiveAsync(page, "/users");
        await page.GetByRole(AriaRole.Button, new() { Name = "+ New user" }).ClickAsync();
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync(username);
        await Forms.SelectAsync(page, "Role", role);
        await page.GetByLabel("Initial password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).ClickAsync();

        // Scoped to the TABLE ROW, not to any text on the page. A bare GetByText(username) matches
        // twice — the "Created <name>." confirmation banner and the row itself — which Playwright
        // reports as a strict-mode violation rather than picking one. That is the right behaviour and
        // it caught a genuinely weak assertion: the banner appears whether or not the user was
        // actually persisted, so matching it would have proved nothing.
        await Assertions.Expect(page.Locator("tr", new() { HasText = username }).First).ToBeVisibleAsync();
    }

    private static async Task DeleteUserAsync(IPage page, string username)
    {
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        await Forms.GotoInteractiveAsync(page, "/users");
        var row = page.Locator("tr", new() { HasText = username });
        if (await row.CountAsync() == 0) return;

        await row.First.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("tr", new() { HasText = username })).ToHaveCountAsync(0);
    }

    private static async Task DeleteStatusPageAsync(IPage page, string slug)
    {
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        await Forms.GotoInteractiveAsync(page, "/status-pages");
        var row = page.Locator("tr", new() { HasText = slug });
        if (await row.CountAsync() == 0) return;

        // "Edit" BY NAME. The first link in this row is the public "View" link, which carries
        // target="_blank": clicking it opens a NEW TAB and leaves this page sitting on the list,
        // where there is no Delete button — so the test waited thirty seconds for a button that was
        // never going to appear, on a page it had never left.
        await row.First.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/edit", StringComparison.Ordinal));
    }
}

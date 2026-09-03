using Microsoft.EntityFrameworkCore;

namespace MT.Uptime.Web.Hosting;

/// <summary>
/// The parts of the MT-Uptime host a different composition root may legitimately want to change.
/// <para>
/// Everything <i>not</i> on this class is fixed: the cookie policy and its session-stamp revocation,
/// the authenticated-by-default fallback, the rate-limit policies, the CSP and the other security
/// headers, forwarded-header handling, the stale-antiforgery recovery, the Blazor circuit gate. Those
/// are the security pipeline, several lines of which exist because of specific review findings, and a
/// host that could opt out of them would eventually be a host that had.
/// </para>
/// <para>
/// The defaults are exactly what the shipped self-hosted server uses, so
/// <c>builder.AddMtUptime()</c> with no arguments produces the product as distributed.
/// </para>
/// </summary>
public sealed class MtUptimeOptions
{
    /// <summary>
    /// How the database is configured. Defaults to SQLite at <see cref="DatabasePath"/> with the
    /// pragma interceptor, which is the shipped arrangement.
    /// <para>
    /// Replaceable because "one file on this machine" is not the only sensible answer — a host running
    /// several instances against one server needs a different provider and a different connection
    /// string, and having to fork the whole host to say so would be absurd.
    /// </para>
    /// </summary>
    public Action<DbContextOptionsBuilder>? ConfigureDatabase { get; set; }

    /// <summary>
    /// Where the SQLite database lives. Ignored when <see cref="ConfigureDatabase"/> is supplied and
    /// the provider is not file-backed.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>Where Data Protection keys are persisted. These sign auth cookies and decrypt every stored secret.</summary>
    public string? DataProtectionKeysPath { get; set; }

    /// <summary>
    /// Whether to create and migrate the database during startup.
    /// <para>
    /// True for a single server that owns its own database. <b>Set false when several instances share
    /// one database</b> — they would otherwise race each other to migrate it at startup, and the
    /// migration must instead be applied once, deliberately, before any of them run.
    /// </para>
    /// </summary>
    public bool MigrateDatabaseAtStartup { get; set; } = true;

    /// <summary>
    /// Whether an instance with no accounts opens the first-run setup wizard.
    /// <para>
    /// True for a server someone installs themselves: an empty user table is not authorization, so the
    /// wizard is gated by a one-time token printed at startup. <b>Set false where accounts are created
    /// by something else</b> — the wizard would be a second, unnecessary way to claim an instance, and
    /// the token it prints would go to a log nobody is reading.
    /// </para>
    /// </summary>
    public bool EnableFirstRunSetup { get; set; } = true;

    /// <summary>
    /// Whether to map the database administration endpoints, which include the online backup.
    /// <para>
    /// They are already behind Admin authorization, but <b>the backup hands out the entire database
    /// file</b> — every password hash and every decryptable secret in it. That is exactly right for an
    /// operator backing up their own instance, and wrong wherever the database is not solely theirs.
    /// </para>
    /// </summary>
    public bool MapDatabaseAdminEndpoints { get; set; } = true;

    /// <summary>
    /// Whether to register the push/heartbeat ingest endpoint. Left on by default; it is part of the
    /// product, and the ping token is its credential.
    /// </summary>
    public bool MapPushEndpoints { get; set; } = true;
}

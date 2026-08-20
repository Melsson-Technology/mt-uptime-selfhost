namespace MT.Uptime.Core.Monitoring.Configs;

/// <summary>
/// Shared config for MySQL and PostgreSQL monitors. The <see cref="Password"/> is stored
/// <b>encrypted</b> (Data Protection) and decrypted by the checker at connect time.
/// </summary>
public sealed class DbMonitorConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }

    /// <summary>Encrypted password ciphertext (never plaintext at rest).</summary>
    public string? Password { get; set; }

    /// <summary>
    /// How the connection to the monitored database is protected. See <see cref="DbTlsMode"/>.
    /// <para>
    /// Defaults to <see cref="DbTlsMode.Preferred"/>, which is what both drivers do when nothing says
    /// otherwise and therefore what every monitor created before this field existed was already doing.
    /// It is <em>not</em> the safe choice — see the enum — but changing the default would silently start
    /// failing every monitor pointed at a database without TLS, which for a monitoring tool means
    /// inventing an outage. The choice is now the operator's to make per monitor.
    /// </para>
    /// <para>
    /// <b>Revisited 2026-08-18, before the repository went public, and deliberately kept.</b> That was
    /// the last cheap moment to change it: the argument for moving to <see cref="DbTlsMode.Required"/>
    /// is that it should be decided before there are installs to break, and after publication there
    /// are. It was kept anyway, because the failure modes are not symmetrical. Leaving it means a
    /// monitor an operator chose to point at a plaintext database keeps working and is not protected
    /// against an on-path attacker — a risk they can see in the editor and opt out of. Changing it means
    /// existing monitors start reporting a database as Down when nothing about the database changed, and
    /// a monitoring product that manufactures outages is not merely wrong, it is wrong in the way that
    /// gets its alerts ignored. Defence that arrives as a false page is not defence.
    /// </para>
    /// <para>
    /// This is a product decision, not a security one, and it is recorded here rather than in a document
    /// so that it reads as chosen rather than inherited. Reversing it is this one initialiser plus a
    /// migration note for existing monitors; nothing else in the code assumes the current value.
    /// </para>
    /// </summary>
    public DbTlsMode Tls { get; set; } = DbTlsMode.Preferred;
}

/// <summary>
/// Transport protection for a database monitor's connection. The numeric values are <b>persisted</b>
/// inside <c>Monitor.ConfigJson</c>, so they must not be renumbered.
/// </summary>
public enum DbTlsMode
{
    /// <summary>
    /// Encrypt if the server offers it, otherwise continue in plaintext, and never check the
    /// certificate. This is the drivers' own default (MySqlConnector <c>Preferred</c>, Npgsql
    /// <c>Prefer</c>) and it protects against nothing active: an on-path attacker either strips the
    /// TLS offer and reads the credentials in clear, or presents any certificate at all and terminates
    /// the session themselves. Reasonable only where the network between here and the database is
    /// already trusted — a loopback or private-subnet database.
    /// </summary>
    Preferred = 0,

    /// <summary>
    /// Require encryption, but do not verify the certificate. Stops a passive reader and a downgrade to
    /// plaintext; does not stop an attacker who can answer for the address, since any certificate is
    /// accepted. The right setting for a database presenting a self-signed certificate.
    /// </summary>
    Required = 1,

    /// <summary>
    /// Require encryption and verify the certificate chain, but not the hostname. Useful with a private
    /// CA whose certificates carry names that do not match how the host is addressed.
    /// </summary>
    VerifyCa = 2,

    /// <summary>
    /// Require encryption and verify both the chain and the hostname. The only mode that resists an
    /// on-path attacker, and the right choice for any database reached over a network you do not
    /// control — a managed cloud database above all.
    /// </summary>
    VerifyFull = 3,
}

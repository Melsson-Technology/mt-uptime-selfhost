using System.Security.Cryptography;
using System.Text;

namespace MT.Uptime.Web.Security;

/// <summary>
/// Guards the first-run setup wizard with a one-time token written to the state directory.
/// <para>
/// <c>POST /auth/setup</c> is necessarily anonymous — it runs before any account exists — and it mints an
/// <see cref="UserRole.Admin"/>. On its own, "the Users table is empty" is not authorization: it is a
/// condition any passer-by can observe, because the first-run guard redirects every page to
/// <c>/setup</c>. That leaves a race, and the attacker does not have to win it fairly:
/// </para>
/// <list type="bullet">
/// <item>Every fresh install is exposed from the moment the reverse proxy starts serving until the
/// operator finishes typing. Requesting a certificate publishes the hostname to Certificate Transparency
/// logs, which are scanned within seconds, so "nobody knows this host yet" is not true.</item>
/// <item>The documented account-recovery path (delete the rows, restart, redo the wizard) reopens the
/// window on a host that is already known — and hands the winner a *populated* instance, keys included.</item>
/// </list>
/// <para>
/// So possession of a secret only readable on the server is required as well. This is the same shape as
/// Jenkins' <c>initialAdminPassword</c>, and for the same reason.
/// </para>
/// </summary>
public sealed class SetupToken(string stateDirectory, ILogger<SetupToken> log)
{
    /// <summary>Filename inside the state directory. Sits beside the database and the key ring, which the deployment already keeps at 0700.</summary>
    public const string FileName = "setup-token";

    private readonly string _path = Path.Combine(stateDirectory, FileName);

    /// <summary>Where the operator will find the token. Surfaced in logs so the prompt is actionable.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Called at startup once the database is ready. Creates and announces a token when the instance is
    /// in first-run state, and clears any leftover file when it is not.
    /// <para>
    /// An existing file is reused rather than regenerated, so restarting mid-setup does not invalidate a
    /// token the operator has already copied.
    /// </para>
    /// </summary>
    public async Task EnsureAsync(bool anyUserExists, CancellationToken ct = default)
    {
        if (anyUserExists)
        {
            Clear();
            return;
        }

        if (!File.Exists(_path))
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await File.WriteAllTextAsync(_path, token, ct);
            Protect(_path);
        }

        var current = (await File.ReadAllTextAsync(_path, ct)).Trim();

        // Logged at Warning so it survives a default log level and stands out in `journalctl -u mt-uptime`
        // or `docker compose logs`. It is a bootstrap secret on a box whose logs are already operator-only,
        // and an operator who cannot find it cannot complete setup at all.
        log.LogWarning(
            "First-run setup is open. Complete it at /setup using this one-time token:\n\n    {Token}\n\n" +
            "It is also readable at {Path}, and is destroyed once the administrator account is created.",
            current, _path);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> matches the outstanding token. False when no token is
    /// outstanding, so setup cannot be completed on an instance that never announced one.
    /// </summary>
    public bool Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        string expected;
        try
        {
            if (!File.Exists(_path)) return false;
            expected = File.ReadAllText(_path).Trim();
        }
        catch (IOException)
        {
            return false;
        }

        if (expected.Length == 0) return false;

        // Fixed-time compare. The token is 256 bits of randomness so a timing oracle is not the likely
        // break, but this costs nothing and the endpoint is anonymous.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(candidate.Trim()));
    }

    /// <summary>Destroys the token. Called once an administrator exists, making the wizard unreachable.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException e)
        {
            // Not fatal: /auth/setup also refuses once any account exists, so a surviving file grants
            // nothing. Still worth saying out loud, because a stale secret on disk is untidy.
            log.LogWarning(e, "Could not remove the spent setup token at {Path}. Delete it by hand.", _path);
        }
    }

    /// <summary>
    /// Restricts the file to the service account on Unix. The state directory is already 0700 in the
    /// documented deployments, so this is defence in depth for installs that placed it elsewhere.
    /// </summary>
    private static void Protect(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // Best effort — a filesystem that cannot express the mode is not a reason to refuse to start.
        }
    }
}

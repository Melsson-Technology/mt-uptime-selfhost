namespace MT.Uptime.Core.Domain;

/// <summary>
/// What an account may do. The numeric values are ordered by privilege and <b>persisted</b>, so they
/// must not be renumbered.
/// <para>
/// <see cref="Viewer"/> is deliberately 0: it is the value a row takes if a default is ever forgotten,
/// a column is added without a backfill, or JSON deserialization sees no value. Failing closed there
/// costs someone an access request; failing open hands out administration silently.
/// </para>
/// </summary>
public enum UserRole
{
    /// <summary>Read-only: dashboard, monitor detail, own profile.</summary>
    Viewer = 0,

    /// <summary>Everything a Viewer can do, plus managing monitors, channels and status pages.</summary>
    Editor = 1,

    /// <summary>Everything, including accounts, instance settings, backup and export.</summary>
    Admin = 2,
}

/// <summary>A user account. "No rows" signals first-run setup, which creates an <see cref="UserRole.Admin"/>.</summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>What this account may do. See <see cref="UserRole"/> on why the default is the weakest.</summary>
    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>Friendly name shown in the UI. Falls back to <see cref="Username"/> when unset.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Where password-reset links are sent. Collected during first-run setup and editable on the
    /// profile page. Without it, reset by email is impossible and the only recovery is the local
    /// escape hatch documented in the deploy README.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// SHA-256 of the outstanding password-reset token, never the token itself — a leaked database
    /// must not hand over the ability to reset the account. Cleared once used or superseded.
    /// </summary>
    public string? PasswordResetTokenHash { get; set; }

    /// <summary>Expiry of the outstanding reset token. Past this, the token is refused.</summary>
    public DateTime? PasswordResetExpiresAt { get; set; }

    /// <summary>
    /// Bumped whenever this account's existing sessions must stop working: a password change or reset,
    /// an admin setting the password, and a role change. The value is stamped into the auth cookie at
    /// sign-in and re-checked on every request, so a cookie carrying a stale stamp is rejected.
    /// <para>
    /// Without this the cookie is entirely self-contained and nothing revokes it — deleting the account
    /// or changing its password left the old session working until it expired, which made the two
    /// remedies the UI offers ("Delete" and "Set password") do nothing an attacker would notice.
    /// </para>
    /// <para>
    /// It counts rather than storing a random value so a stale cookie can never collide with a current
    /// one, and starts at 0 because that is what an un-backfilled row takes — a row created before this
    /// column existed matches a cookie that carries no stamp claim only if the claim is absent, which
    /// <c>ValidateSessionAsync</c> treats as a rejection.
    /// </para>
    /// </summary>
    public int SessionStamp { get; set; }
}

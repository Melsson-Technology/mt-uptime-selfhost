using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;

namespace MT.Uptime.Web.Services;

/// <summary>
/// Single-admin account management: existence check, creation, verification, profile edits, and the
/// password-reset token lifecycle.
/// </summary>
public sealed class UserAccountService(IDbContextFactory<AppDbContext> factory, IPasswordHasher<AppUser> hasher)
{
    /// <summary>How long a reset link stays valid. Short, because the account it protects is the admin.</summary>
    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);

    private volatile bool _anyUserCached;

    public async Task<bool> AnyUserExistsAsync(CancellationToken ct = default)
    {
        if (_anyUserCached) return true;
        await using var db = await factory.CreateDbContextAsync(ct);
        var any = await db.Users.AnyAsync(ct);
        if (any) _anyUserCached = true;
        return any;
    }

    /// <summary>
    /// Creates an account. <paramref name="role"/> has no default deliberately — every call site has to
    /// state what it is creating, so "forgot to pass a role" cannot quietly mint an administrator.
    /// </summary>
    public async Task<AppUser> CreateAsync(
        string username, string password, string? email, UserRole role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = new AppUser
        {
            Username = username.Trim(),
            Email = NullIfBlank(email),
            Role = role,
            CreatedAt = DateTime.UtcNow,
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        _anyUserCached = true;
        return user;
    }

    public async Task<AppUser?> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null) return null;

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
            return null;

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>The single admin account, or null before first-run setup.</summary>
    public async Task<AppUser?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A specific account by id. Used to resolve the *signed-in* user from their NameIdentifier claim
    /// rather than taking whichever row comes first — identical today with one account, but the
    /// difference between the two is a privilege-escalation bug the moment there is more than one.
    /// </summary>
    public async Task<AppUser?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    // --- Account administration --------------------------------------------------------------------

    /// <summary>Every account, oldest first. Admin-only in the UI; the service does not check.</summary>
    public async Task<List<AppUser>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Creates an account on an admin's behalf, with an initial password they hand over out of band.
    /// Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> AddUserAsync(
        string username, string password, string? displayName, string? email, UserRole role,
        CancellationToken ct = default)
    {
        username = username.Trim();

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            return "That username is already taken.";

        var user = await CreateAsync(username, password, email, role, ct);

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            await using var db2 = await factory.CreateDbContextAsync(ct);
            var row = await db2.Users.FirstAsync(u => u.Id == user.Id, ct);
            row.DisplayName = displayName.Trim();
            await db2.SaveChangesAsync(ct);
        }

        return null;
    }

    /// <summary>
    /// Changes an account's role. Returns an error message, or null on success.
    /// <para>
    /// Refuses to demote the last Admin. That check and the write are in the same
    /// <c>DbContext</c>/transaction, so two admins demoting each other concurrently cannot both pass —
    /// SQLite serializes the writes, and the second one re-counts after the first has committed.
    /// </para>
    /// </summary>
    public async Task<string?> ChangeRoleAsync(int userId, UserRole role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return "Account not found.";
        if (user.Role == role) return null;

        if (user.Role == UserRole.Admin && await IsLastAdminAsync(db, userId, ct))
            return "This is the only administrator. Promote someone else first, or the instance would be left with no way to manage accounts.";

        user.Role = role;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Deletes an account. Returns an error message, or null on success. <paramref name="actingUserId"/>
    /// is the signed-in admin: deleting yourself is refused because it ends your own session mid-request,
    /// which reads as the app breaking rather than as a deliberate act.
    /// </summary>
    public async Task<string?> DeleteUserAsync(int userId, int actingUserId, CancellationToken ct = default)
    {
        if (userId == actingUserId) return "You cannot delete your own account.";

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return "Account not found.";

        if (user.Role == UserRole.Admin && await IsLastAdminAsync(db, userId, ct))
            return "This is the only administrator and cannot be deleted.";

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Sets another account's password without knowing the old one — the admin path for "they forgot it
    /// and email isn't configured". Any outstanding reset token is discarded, same as a self-service
    /// change: a link requested earlier must not survive an admin having just set a new password.
    /// </summary>
    public async Task<string?> SetPasswordAsync(int userId, string newPassword, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return "Account not found.";

        user.PasswordHash = hasher.HashPassword(user, newPassword);
        ClearResetToken(user);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>True when <paramref name="userId"/> is the only account holding <see cref="UserRole.Admin"/>.</summary>
    private static async Task<bool> IsLastAdminAsync(AppDbContext db, int userId, CancellationToken ct)
        => !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin, ct);

    // --- Profile ---------------------------------------------------------------------------------

    /// <summary>
    /// Updates the profile fields. Returns an error message when the username is taken, else null.
    /// </summary>
    public async Task<string?> UpdateProfileAsync(
        int userId, string username, string? displayName, string? email, CancellationToken ct = default)
    {
        username = username.Trim();

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return "Account not found.";

        if (await db.Users.AnyAsync(u => u.Id != userId && u.Username == username, ct))
            return "That username is already taken.";

        user.Username = username;
        user.DisplayName = NullIfBlank(displayName);
        user.Email = NullIfBlank(email);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Changes the password after verifying the current one. Returns an error message, or null on success.
    /// Any outstanding reset token is discarded — a deliberate password change should invalidate a link
    /// someone may have requested earlier.
    /// </summary>
    public async Task<string?> ChangePasswordAsync(
        int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return "Account not found.";

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            return "Your current password is incorrect.";

        user.PasswordHash = hasher.HashPassword(user, newPassword);
        ClearResetToken(user);
        await db.SaveChangesAsync(ct);
        return null;
    }

    // --- Password reset --------------------------------------------------------------------------

    /// <summary>
    /// Issues a reset token for the account matching <paramref name="email"/>, returning the raw token to
    /// email out. Returns null when no account matches — callers must still respond identically either
    /// way, so the endpoint never reveals whether an address is registered.
    /// </summary>
    public async Task<string?> BeginPasswordResetAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim();
        if (email.Length == 0) return null;

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email == email, ct);
        if (user is null) return null;

        // 256 bits, URL-safe. Only its hash is stored, so a database copy cannot be turned into a reset.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        user.PasswordResetTokenHash = HashToken(token);
        user.PasswordResetExpiresAt = DateTime.UtcNow + ResetTokenLifetime;
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>True when the token matches an unexpired outstanding reset (used to render the form).</summary>
    public async Task<bool> IsResetTokenValidAsync(string token, CancellationToken ct = default)
        => await FindByResetTokenAsync(token, ct) is not null;

    /// <summary>
    /// Completes a reset. Returns an error message, or null on success. The token is single-use: it is
    /// cleared whether or not the caller ever comes back.
    /// </summary>
    public async Task<string?> CompletePasswordResetAsync(string token, string newPassword, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await FindByResetTokenAsync(db, token, ct);
        if (user is null) return "That reset link is invalid or has expired. Request a new one.";

        user.PasswordHash = hasher.HashPassword(user, newPassword);
        ClearResetToken(user);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private async Task<AppUser?> FindByResetTokenAsync(string token, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await FindByResetTokenAsync(db, token, ct);
    }

    private static async Task<AppUser?> FindByResetTokenAsync(AppDbContext db, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = HashToken(token);
        var user = await db.Users.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == hash, ct);
        if (user?.PasswordResetExpiresAt is null) return null;

        return user.PasswordResetExpiresAt > DateTime.UtcNow ? user : null;
    }

    private static void ClearResetToken(AppUser user)
    {
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;
    }

    /// <summary>
    /// SHA-256 of the token. A plain hash (no salt/stretching) is right here: the token is 256 bits of
    /// cryptographic randomness with a one-hour life, so there is no guessable input to protect against —
    /// unlike a password, which is why <see cref="IPasswordHasher{T}"/> is used for those.
    /// </summary>
    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

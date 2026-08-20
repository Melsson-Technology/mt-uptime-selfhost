using System.Collections.Concurrent;
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

    /// <summary>
    /// Backstop lifetime for a cached session stamp. Every write that revokes sessions goes through this
    /// singleton and evicts the entry directly, so this only bounds staleness from a change made *outside*
    /// the app — someone editing the database by hand, which is the documented break-glass path.
    /// </summary>
    private static readonly TimeSpan StampCacheLifetime = TimeSpan.FromSeconds(30);

    private volatile bool _anyUserCached;

    /// <summary>
    /// userId → (current stamp, when this entry goes stale). Read on every authenticated request, so it
    /// exists to keep <see cref="ValidateSessionAsync"/> off the database on the hot path. A miss costs
    /// one indexed lookup; a revoking write evicts the entry, so revocation is immediate rather than
    /// waiting out <see cref="StampCacheLifetime"/>.
    /// </summary>
    private readonly ConcurrentDictionary<int, (int Stamp, DateTime ExpiresAt)> _stampCache = new();

    /// <summary>
    /// A throwaway account and hash used to burn the same KDF time when no user matched, so an unknown
    /// username cannot be told from a wrong password by how long the answer took. Computed once from
    /// fresh randomness — the value never has to verify against anything, it only has to cost the same.
    /// </summary>
    private static readonly AppUser DummyUser = new() { Username = "" };

    private readonly string DummyHash = hasher.HashPassword(
        DummyUser, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

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
        if (user is null)
        {
            // Verify against a throwaway hash before giving up. Returning here directly costs one indexed
            // lookup (~1 ms) while a real username costs a PBKDF2 verification (~80 ms), and that 80x gap
            // is a username oracle needing no statistics and one probe per candidate — which defeats the
            // whole point of the login page answering identically for every kind of failure.
            hasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return null;
        }

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

        // Same shape as the username check above, and needed for the same reason: Email carries a unique
        // index, so without this an admin who reuses an address gets a DbUpdateException out of
        // CreateAsync rather than a sentence explaining what they typed wrong. Neither check is the
        // guard — the index is, and it is what holds if two admins race — but a constraint violation is
        // not an error message.
        var normalisedEmail = NullIfBlank(email);
        if (normalisedEmail is not null && await db.Users.AnyAsync(u => u.Email == normalisedEmail, ct))
            return "That email address is already in use by another account.";

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
    /// Refuses to demote the last Admin — see <see cref="UnlessLastAdmin"/> for why that refusal rides
    /// in the <c>UPDATE</c> itself rather than being checked beforehand.
    /// </para>
    /// </summary>
    public async Task<string?> ChangeRoleAsync(int userId, UserRole role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var current = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.Role)
            .FirstOrDefaultAsync(ct);
        if (current is null) return "Account not found.";
        // Re-selecting the role an account already holds stays a silent no-op, and has to be settled
        // before the write: the sole Admin's own row fails the guard below by construction, so without
        // this the dropdown would report an error for a change nobody asked for.
        if (current == role) return null;

        var changed = await UnlessLastAdmin(db, userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.Role, role)
            // The role is a cookie claim, so without this a demotion would not bind until the affected
            // user happened to sign in again. Bumping the stamp ends their session instead, which is the
            // only way the change takes effect at the moment it is made. The database computes the
            // increment rather than a tracked entity, so a concurrent bump cannot be read stale and
            // written back over.
            .SetProperty(u => u.SessionStamp, u => u.SessionStamp + 1), ct);

        // Zero rows is the refusal. It also covers the account being deleted between the two statements,
        // which this message reads slightly wrong for — the page reloads the list either way.
        if (changed == 0)
            return "This is the only administrator. Promote someone else first, or the instance would be left with no way to manage accounts.";

        // Evicted after the write, not before it: the new stamp is committed by the time this runs, so
        // the next reader repopulates from it rather than re-caching the value it just superseded.
        _stampCache.TryRemove(userId, out _);
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
        var deleted = await UnlessLastAdmin(db, userId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            // Only the refusal path pays for telling the two cases apart, so the ordinary delete stays
            // a single statement.
            return await db.Users.AnyAsync(u => u.Id == userId, ct)
                ? "This is the only administrator and cannot be deleted."
                : "Account not found.";

        // ValidateSessionAsync already fails closed on a missing row, but the cache would otherwise keep
        // answering from the deleted account's last known stamp until the entry aged out.
        _stampCache.TryRemove(userId, out _);
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
        RevokeSessions(user);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Narrows a query to <paramref name="userId"/>, but only while removing that account's Admin role
    /// would leave another Admin behind.
    /// <para>
    /// The invariant rides in the statement's <c>WHERE</c> clause instead of being checked first because
    /// a check and a write are two statements on two connections: two admins demoting or deleting each
    /// other at the same moment both pass the check, both are told it worked, and the instance is left
    /// with no administrator at all. That state cannot be repaired from the application — managing
    /// accounts requires an Admin, and first-run setup will not offer to help because accounts still
    /// exist. As one statement SQLite serialises the writers, so the second re-evaluates the
    /// <c>EXISTS</c> against the first's committed result and matches nothing.
    /// </para>
    /// <para><b>Zero rows affected is the refusal</b>, and callers must read it as one.</para>
    /// </summary>
    private static IQueryable<AppUser> UnlessLastAdmin(AppDbContext db, int userId)
        => db.Users.Where(u => u.Id == userId
            && (u.Role != UserRole.Admin || db.Users.Any(o => o.Id != userId && o.Role == UserRole.Admin)));

    // --- Session validity ---------------------------------------------------------------------------

    /// <summary>
    /// Whether a cookie presented by <paramref name="userId"/> carrying <paramref name="stamp"/> is still
    /// good. False when the account has been deleted, or when anything since sign-in bumped the stamp
    /// (password change or reset, an admin setting the password, a role change).
    /// <para>
    /// This runs on every authenticated request, so it answers from <see cref="_stampCache"/> where it
    /// can. Fails closed: an account that cannot be read is not authenticated.
    /// </para>
    /// </summary>
    public async Task<bool> ValidateSessionAsync(int userId, int stamp, CancellationToken ct = default)
    {
        if (_stampCache.TryGetValue(userId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Stamp == stamp;

        await using var db = await factory.CreateDbContextAsync(ct);
        var current = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (int?)u.SessionStamp)
            .FirstOrDefaultAsync(ct);

        // Deleted account. Drop any cached entry so a later re-creation at the same id cannot inherit it.
        if (current is null)
        {
            _stampCache.TryRemove(userId, out _);
            return false;
        }

        _stampCache[userId] = (current.Value, DateTime.UtcNow + StampCacheLifetime);
        return current.Value == stamp;
    }

    /// <summary>
    /// Invalidates every existing session for an account by advancing its stamp. Call this on the same
    /// tracked entity as the change that motivated it, so the bump and the change commit together — a
    /// password that changed without the bump would leave the old cookie working.
    /// </summary>
    private void RevokeSessions(AppUser user)
    {
        user.SessionStamp++;
        // Evict rather than update: the write has not committed yet, and a concurrent reader must not
        // see the new stamp until it has. The next read repopulates from the committed row.
        _stampCache.TryRemove(user.Id, out _);
    }

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

        // Excludes this account, so re-saving your own profile without touching the address is not a
        // collision with yourself. See AddUserAsync for why the check exists alongside the index.
        var normalisedEmail = NullIfBlank(email);
        if (normalisedEmail is not null
            && await db.Users.AnyAsync(u => u.Id != userId && u.Email == normalisedEmail, ct))
            return "That email address is already in use by another account.";

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
        RevokeSessions(user);
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
        // Ordered explicitly. A unique index on Email now makes more than one match impossible, but this
        // statement is what decides which account a reset link recovers, and "whichever row the engine
        // returned first" is not a rule — it held only because the administrator happened to be row 1.
        // Both halves are cheap; relying on either one alone is what made this worth writing down.
        var user = await db.Users
            .Where(u => u.Email != null && u.Email == email)
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct);
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
        RevokeSessions(user);
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

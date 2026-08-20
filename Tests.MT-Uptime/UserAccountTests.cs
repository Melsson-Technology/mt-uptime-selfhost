using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Domain;
using MT.Uptime.Web.Services;

namespace MT.Uptime.Tests;

/// <summary>
/// Account management and the password-reset token lifecycle, against a real SQLite database.
/// Security-sensitive: these assert that the raw token never reaches storage, that a reset is
/// single-use and expiring, and that a password change invalidates any outstanding link.
/// </summary>
public class UserAccountTests
{
    private static UserAccountService NewService(TestDatabase db)
        => new(db, new PasswordHasher<AppUser>());

    private static async Task<(TestDatabase Db, UserAccountService Svc, AppUser User)> SetupAsync(
        string email = "admin@example.com")
    {
        var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);
        var user = await svc.CreateAsync("admin", "correct-horse", email, UserRole.Admin);
        return (db, svc, user);
    }

    // --- Creation and verification ---------------------------------------------------------------

    [Fact]
    public async Task A_created_account_verifies_with_its_password_and_rejects_others()
    {
        var (db, svc, _) = await SetupAsync();
        await using var _db = db;

        Assert.NotNull(await svc.VerifyAsync("admin", "correct-horse"));
        Assert.Null(await svc.VerifyAsync("admin", "wrong"));
        Assert.Null(await svc.VerifyAsync("nobody", "correct-horse"));
    }

    [Fact]
    public async Task Signing_in_is_case_insensitive_in_the_username()
    {
        // Under SQLite's default binary collation this failed, and the login page reported it as
        // "Invalid username or password" — which sends the operator after a password that was never
        // wrong, and which no amount of resetting the password will fix.
        var db = await TestDatabase.CreateAsync();
        await using var _db = db;
        var svc = NewService(db);
        await svc.CreateAsync("Matt", "correct-horse", "matt@example.com", UserRole.Admin);

        Assert.NotNull(await svc.VerifyAsync("Matt", "correct-horse"));
        Assert.NotNull(await svc.VerifyAsync("matt", "correct-horse"));
        Assert.NotNull(await svc.VerifyAsync("MATT", "correct-horse"));

        // Still the same account, and the password still has to be right.
        Assert.Equal("Matt", (await svc.VerifyAsync("mAtT", "correct-horse"))!.Username);
        Assert.Null(await svc.VerifyAsync("matt", "wrong"));
    }

    [Fact]
    public async Task Two_accounts_differing_only_by_case_cannot_both_exist()
    {
        // The other half of a case-insensitive unique index, and the reason it is wanted: two accounts
        // you can only tell apart by capitalisation are a phishing surface, not a feature.
        var db = await TestDatabase.CreateAsync();
        await using var _db = db;
        var svc = NewService(db);
        await svc.CreateAsync("Matt", "correct-horse", null, UserRole.Admin);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => svc.CreateAsync("matt", "another-password", null, UserRole.Viewer));
    }

    [Fact]
    public async Task The_password_is_never_stored_in_the_clear()
    {
        var (db, _, _) = await SetupAsync();
        await using var _db = db;

        await using var ctx = db.CreateDbContext();
        var stored = await ctx.Users.SingleAsync();
        Assert.DoesNotContain("correct-horse", stored.PasswordHash);
    }

    // --- Profile ---------------------------------------------------------------------------------

    [Fact]
    public async Task Updating_the_profile_persists_all_three_fields()
    {
        var (db, svc, user) = await SetupAsync();
        await using var _db = db;

        Assert.Null(await svc.UpdateProfileAsync(user.Id, "newname", "Matt", "matt@example.com"));

        var updated = await svc.GetAsync();
        Assert.Equal("newname", updated!.Username);
        Assert.Equal("Matt", updated.DisplayName);
        Assert.Equal("matt@example.com", updated.Email);
        // The new username is what signs in from now on.
        Assert.NotNull(await svc.VerifyAsync("newname", "correct-horse"));
    }

    [Fact]
    public async Task Blank_profile_fields_are_stored_as_null_not_empty_strings()
    {
        var (db, svc, user) = await SetupAsync();
        await using var _db = db;

        await svc.UpdateProfileAsync(user.Id, "admin", "   ", "");

        var updated = await svc.GetAsync();
        Assert.Null(updated!.DisplayName);
        Assert.Null(updated.Email);
    }

    [Fact]
    public async Task Changing_the_password_requires_the_current_one()
    {
        var (db, svc, user) = await SetupAsync();
        await using var _db = db;

        var error = await svc.ChangePasswordAsync(user.Id, "not-my-password", "brand-new-secret");
        Assert.NotNull(error);
        Assert.Contains("current password", error);
        // Unchanged.
        Assert.NotNull(await svc.VerifyAsync("admin", "correct-horse"));

        Assert.Null(await svc.ChangePasswordAsync(user.Id, "correct-horse", "brand-new-secret"));
        Assert.NotNull(await svc.VerifyAsync("admin", "brand-new-secret"));
        Assert.Null(await svc.VerifyAsync("admin", "correct-horse"));
    }

    // --- Password reset --------------------------------------------------------------------------

    [Fact]
    public async Task A_reset_token_is_issued_for_a_known_address_and_stored_only_as_a_hash()
    {
        var (db, svc, _) = await SetupAsync("admin@example.com");
        await using var _db = db;

        var token = await svc.BeginPasswordResetAsync("admin@example.com");
        Assert.NotNull(token);
        Assert.Equal(64, token!.Length); // 256 bits as hex

        await using var ctx = db.CreateDbContext();
        var stored = await ctx.Users.SingleAsync();
        // The emailed token must not be recoverable from the database.
        Assert.NotEqual(token, stored.PasswordResetTokenHash);
        Assert.NotNull(stored.PasswordResetTokenHash);
        Assert.NotNull(stored.PasswordResetExpiresAt);
    }

    [Fact]
    public async Task No_token_is_issued_for_an_unknown_address()
    {
        var (db, svc, _) = await SetupAsync("admin@example.com");
        await using var _db = db;

        Assert.Null(await svc.BeginPasswordResetAsync("stranger@example.com"));
        Assert.Null(await svc.BeginPasswordResetAsync(""));
    }

    [Fact]
    public async Task An_account_with_no_email_cannot_be_reset()
    {
        var db = await TestDatabase.CreateAsync();
        await using var _db = db;
        var svc = NewService(db);
        await svc.CreateAsync("admin", "correct-horse", email: null, UserRole.Admin);

        // Notably, a blank request must not match the null-email account.
        Assert.Null(await svc.BeginPasswordResetAsync(""));
        Assert.Null(await svc.BeginPasswordResetAsync("admin@example.com"));
    }

    [Fact]
    public async Task A_valid_token_sets_the_new_password_once_and_then_stops_working()
    {
        var (db, svc, _) = await SetupAsync();
        await using var _db = db;

        var token = await svc.BeginPasswordResetAsync("admin@example.com");
        Assert.True(await svc.IsResetTokenValidAsync(token!));

        Assert.Null(await svc.CompletePasswordResetAsync(token!, "a-fresh-password"));
        Assert.NotNull(await svc.VerifyAsync("admin", "a-fresh-password"));

        // Single use: the same link must not work twice.
        Assert.False(await svc.IsResetTokenValidAsync(token!));
        var second = await svc.CompletePasswordResetAsync(token!, "another-password");
        Assert.NotNull(second);
        Assert.NotNull(await svc.VerifyAsync("admin", "a-fresh-password")); // still the first reset
    }

    [Fact]
    public async Task A_bogus_token_is_rejected()
    {
        var (db, svc, _) = await SetupAsync();
        await using var _db = db;
        await svc.BeginPasswordResetAsync("admin@example.com");

        Assert.False(await svc.IsResetTokenValidAsync(new string('a', 64)));
        Assert.False(await svc.IsResetTokenValidAsync(""));
        Assert.NotNull(await svc.CompletePasswordResetAsync(new string('a', 64), "nope"));
        // The real password is untouched.
        Assert.NotNull(await svc.VerifyAsync("admin", "correct-horse"));
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var (db, svc, _) = await SetupAsync();
        await using var _db = db;

        var token = await svc.BeginPasswordResetAsync("admin@example.com");

        // Wind the expiry into the past rather than waiting an hour.
        await using (var ctx = db.CreateDbContext())
        {
            var u = await ctx.Users.SingleAsync();
            u.PasswordResetExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await ctx.SaveChangesAsync();
        }

        Assert.False(await svc.IsResetTokenValidAsync(token!));
        Assert.NotNull(await svc.CompletePasswordResetAsync(token!, "too-late"));
        Assert.NotNull(await svc.VerifyAsync("admin", "correct-horse"));
    }

    [Fact]
    public async Task Requesting_a_second_reset_invalidates_the_first_link()
    {
        var (db, svc, _) = await SetupAsync();
        await using var _db = db;

        var first = await svc.BeginPasswordResetAsync("admin@example.com");
        var second = await svc.BeginPasswordResetAsync("admin@example.com");

        Assert.NotEqual(first, second);
        Assert.False(await svc.IsResetTokenValidAsync(first!));
        Assert.True(await svc.IsResetTokenValidAsync(second!));
    }

    [Fact]
    public async Task Changing_the_password_normally_kills_an_outstanding_reset_link()
    {
        var (db, svc, user) = await SetupAsync();
        await using var _db = db;

        var token = await svc.BeginPasswordResetAsync("admin@example.com");
        await svc.ChangePasswordAsync(user.Id, "correct-horse", "chosen-deliberately");

        // Someone who requested a link earlier must not be able to override the deliberate change.
        Assert.False(await svc.IsResetTokenValidAsync(token!));
        Assert.NotNull(await svc.VerifyAsync("admin", "chosen-deliberately"));
    }

    // --- Roles and the last-admin guardrails -------------------------------------------------------

    [Fact]
    public async Task First_run_creates_an_admin_and_added_users_keep_the_role_they_were_given()
    {
        var (db, svc, admin) = await SetupAsync();
        await using var _ = db;

        Assert.Equal(UserRole.Admin, admin.Role);

        Assert.Null(await svc.AddUserAsync("watcher", "watcher-password", null, null, UserRole.Viewer));
        Assert.Null(await svc.AddUserAsync("editorial", "editor-password", "Ed", null, UserRole.Editor));

        var users = await svc.ListAsync();
        Assert.Equal(UserRole.Viewer, users.Single(u => u.Username == "watcher").Role);
        Assert.Equal(UserRole.Editor, users.Single(u => u.Username == "editorial").Role);
        Assert.Equal("Ed", users.Single(u => u.Username == "editorial").DisplayName);
    }

    [Fact]
    public async Task A_duplicate_username_is_refused_rather_than_creating_a_second_account()
    {
        var (db, svc, _) = await SetupAsync();
        await using var _d = db;

        Assert.NotNull(await svc.AddUserAsync("admin", "another-password", null, null, UserRole.Viewer));
        Assert.Single(await svc.ListAsync());
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted()
    {
        // The failure this prevents is unrecoverable through the UI: changing a role requires an admin,
        // so an instance with zero admins can never get one back.
        var (db, svc, admin) = await SetupAsync();
        await using var _ = db;
        await svc.AddUserAsync("watcher", "watcher-password", null, null, UserRole.Viewer);

        Assert.NotNull(await svc.ChangeRoleAsync(admin.Id, UserRole.Viewer));
        Assert.Equal(UserRole.Admin, (await svc.GetByIdAsync(admin.Id))!.Role);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_deleted()
    {
        var (db, svc, admin) = await SetupAsync();
        await using var _ = db;
        await svc.AddUserAsync("watcher", "watcher-password", null, null, UserRole.Viewer);
        var watcher = (await svc.ListAsync()).Single(u => u.Username == "watcher");

        // Deleted by someone else, so the refusal is about being the last admin rather than self-deletion.
        Assert.NotNull(await svc.DeleteUserAsync(admin.Id, actingUserId: watcher.Id));
        Assert.NotNull(await svc.GetByIdAsync(admin.Id));
    }

    [Fact]
    public async Task An_admin_can_be_demoted_or_deleted_once_a_second_admin_exists()
    {
        // The mirror of the two above: the guardrail must not become a permanent lock.
        var (db, svc, admin) = await SetupAsync();
        await using var _ = db;
        await svc.AddUserAsync("second", "second-password", null, null, UserRole.Admin);
        var second = (await svc.ListAsync()).Single(u => u.Username == "second");

        Assert.Null(await svc.ChangeRoleAsync(admin.Id, UserRole.Viewer));
        Assert.Equal(UserRole.Viewer, (await svc.GetByIdAsync(admin.Id))!.Role);

        Assert.Null(await svc.DeleteUserAsync(admin.Id, actingUserId: second.Id));
        Assert.Null(await svc.GetByIdAsync(admin.Id));
    }

    [Fact]
    public async Task You_cannot_delete_your_own_account()
    {
        var (db, svc, admin) = await SetupAsync();
        await using var _ = db;
        await svc.AddUserAsync("second", "second-password", null, null, UserRole.Admin);

        // Not the last-admin rule — a second admin exists, so only the self-deletion check can refuse it.
        Assert.NotNull(await svc.DeleteUserAsync(admin.Id, actingUserId: admin.Id));
        Assert.NotNull(await svc.GetByIdAsync(admin.Id));
    }

    [Fact]
    public async Task An_admin_set_password_works_and_discards_an_outstanding_reset_link()
    {
        var (db, svc, _) = await SetupAsync();
        await using var _d = db;
        await svc.AddUserAsync("watcher", "watcher-password", null, "watcher@example.com", UserRole.Viewer);
        var watcher = (await svc.ListAsync()).Single(u => u.Username == "watcher");

        var token = await svc.BeginPasswordResetAsync("watcher@example.com");
        Assert.NotNull(token);

        Assert.Null(await svc.SetPasswordAsync(watcher.Id, "admin-chosen-password"));

        Assert.NotNull(await svc.VerifyAsync("watcher", "admin-chosen-password"));
        Assert.Null(await svc.VerifyAsync("watcher", "watcher-password"));
        // A link requested before the admin intervened must not survive it.
        Assert.False(await svc.IsResetTokenValidAsync(token!));
    }

    // --- The last-administrator invariant under concurrency ---------------------------------------

    /// <summary>
    /// Two Admins demoting each other at the same moment must not both succeed. The guard used to be a
    /// read followed by a write on separate statements, so both callers observed "another Admin exists",
    /// both wrote, and the instance was left with no administrator at all — unrepairable through the UI,
    /// since managing accounts requires an Admin and first-run setup declines to help while accounts
    /// exist. Reproduced in 147 of 200 unassisted runs before the fix.
    /// </summary>
    [Fact]
    public async Task Two_admins_demoting_each_other_cannot_both_win()
    {
        for (var round = 0; round < 25; round++)
        {
            await using var db = await TestDatabase.CreateAsync();
            var svc = NewService(db);
            var a = await svc.CreateAsync($"a{round}", "pw-a", null, UserRole.Admin);
            var b = await svc.CreateAsync($"b{round}", "pw-b", null, UserRole.Admin);

            // RunContinuationsAsynchronously matters: without it SetResult runs both continuations
            // inline on this thread, serialising the very interleaving the round exists to produce.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(async () => { await start.Task; return await svc.ChangeRoleAsync(b.Id, UserRole.Viewer); });
            var second = Task.Run(async () => { await start.Task; return await svc.ChangeRoleAsync(a.Id, UserRole.Viewer); });
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            var admins = (await svc.ListAsync()).Count(u => u.Role == UserRole.Admin);
            Assert.True(admins >= 1, $"round {round}: the instance was left with no administrator");

            // And the refusal has to be reported, not swallowed: an operator told "done" twice would
            // never learn that one of the two demotions did not happen.
            if (admins == 1)
                Assert.Contains(results, r => r is not null);
        }
    }

    [Fact]
    public async Task Two_admins_deleting_each_other_cannot_both_win()
    {
        for (var round = 0; round < 25; round++)
        {
            await using var db = await TestDatabase.CreateAsync();
            var svc = NewService(db);
            var a = await svc.CreateAsync($"a{round}", "pw-a", null, UserRole.Admin);
            var b = await svc.CreateAsync($"b{round}", "pw-b", null, UserRole.Admin);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Each deletes the other, so neither is refused for deleting itself.
            var first = Task.Run(async () => { await start.Task; return await svc.DeleteUserAsync(b.Id, a.Id); });
            var second = Task.Run(async () => { await start.Task; return await svc.DeleteUserAsync(a.Id, b.Id); });
            start.SetResult();
            await Task.WhenAll(first, second);

            var admins = (await svc.ListAsync()).Count(u => u.Role == UserRole.Admin);
            Assert.True(admins >= 1, $"round {round}: both deletions landed and no administrator remains");
        }
    }

    /// <summary>
    /// The control. Moving the invariant into the statement must not start refusing ordinary edits —
    /// including the sole Admin re-selecting the role they already hold, whose own row fails the guard
    /// by construction and is why the no-op is settled before the write.
    /// </summary>
    [Fact]
    public async Task Ordinary_role_changes_still_work()
    {
        var (db, svc, admin) = await SetupAsync();
        await using var _ = db;
        var helper = await svc.CreateAsync("helper", "pw", null, UserRole.Viewer);

        Assert.Null(await svc.ChangeRoleAsync(helper.Id, UserRole.Editor));
        Assert.Null(await svc.ChangeRoleAsync(admin.Id, UserRole.Admin));   // no-op on the sole Admin
        Assert.NotNull(await svc.ChangeRoleAsync(admin.Id, UserRole.Viewer));   // still refused

        var roles = (await svc.ListAsync()).ToDictionary(u => u.Username, u => u.Role);
        Assert.Equal(UserRole.Editor, roles["helper"]);
        Assert.Equal(UserRole.Admin, roles["admin"]);
    }
}

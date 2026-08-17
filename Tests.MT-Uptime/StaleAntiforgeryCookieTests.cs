using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;

namespace MT.Uptime.Tests;

/// <summary>
/// The rule the stale-cookie middleware turns on: a token encrypted under a different Data Protection
/// key ring must be recognised as unreadable rather than reaching the antiforgery middleware, which
/// answers a bare 400 that a first-time installer cannot act on.
/// </summary>
public class StaleAntiforgeryCookieTests
{
    private const string TokenPurpose = "Microsoft.AspNetCore.Antiforgery.AntiforgeryToken.v1";

    /// <summary>A self-contained key ring, standing in for one instance's <c>keys/</c> directory.</summary>
    private static IDataProtector Ring(string dir) =>
        DataProtectionProvider.Create(new DirectoryInfo(dir)).CreateProtector(TokenPurpose);

    private static string Issue(IDataProtector p, string token = "antiforgery-token") =>
        WebEncoders.Base64UrlEncode(p.Protect(System.Text.Encoding.UTF8.GetBytes(token)));

    private static bool CanDecrypt(IDataProtector p, string cookie)
    {
        try { p.Unprotect(WebEncoders.Base64UrlDecode(cookie)); return true; }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    [Fact]
    public void A_token_from_another_key_ring_is_unreadable()
    {
        // Exactly the production scenario: `docker compose down -v` gives the new container a new ring,
        // while the browser still holds a cookie issued by the old one.
        var a = Directory.CreateTempSubdirectory("mtu-ring-a");
        var b = Directory.CreateTempSubdirectory("mtu-ring-b");
        try
        {
            var oldRing = Ring(a.FullName);
            var newRing = Ring(b.FullName);

            var cookie = Issue(oldRing);

            Assert.True(CanDecrypt(oldRing, cookie));    // the instance that issued it
            Assert.False(CanDecrypt(newRing, cookie));   // a fresh install — this is the 400
        }
        finally
        {
            a.Delete(true);
            b.Delete(true);
        }
    }

    [Fact]
    public void Its_own_token_survives_a_restart_of_the_same_ring()
    {
        // The flip side, and the reason this cannot simply drop every cookie: a container restarting
        // against its existing volume must keep honouring tokens it issued before the restart.
        var dir = Directory.CreateTempSubdirectory("mtu-ring-same");
        try
        {
            var cookie = Issue(Ring(dir.FullName));
            Assert.True(CanDecrypt(Ring(dir.FullName), cookie));   // reopened, same keys/ directory
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64url-!!")]
    [InlineData("YWJjZGVmZ2hpamtsbW5vcA")]   // valid base64url, not a protected payload
    public void Malformed_cookie_values_are_treated_as_unreadable_not_thrown(string value)
    {
        // Whatever a browser sends, this has to return false rather than throw — an exception here would
        // become a 500, which is worse than the 400 it replaces.
        var dir = Directory.CreateTempSubdirectory("mtu-ring-junk");
        try { Assert.False(CanDecrypt(Ring(dir.FullName), value)); }
        finally { dir.Delete(true); }
    }
}

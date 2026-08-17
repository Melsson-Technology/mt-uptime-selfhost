using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;

namespace MT.Uptime.Web.Security;

/// <summary>
/// Discards antiforgery cookies this instance cannot decrypt, so a stale one becomes a fresh token
/// rather than an unexplained <c>400 Bad Request</c>.
/// <para>
/// An antiforgery token is encrypted with the Data Protection key ring. If a browser holds a cookie
/// issued under a <i>different</i> ring, the token cannot be decrypted and ASP.NET Core answers 400 with
/// no explanation and nothing the user can act on. This is not an exotic case:
/// </para>
/// <list type="bullet">
///   <item><c>docker compose down -v</c> then <c>up</c> — new volume, new key ring, old cookie.</item>
///   <item>Restoring a database without <c>keys/</c> beside it.</item>
///   <item>Any other instance previously served on the same hostname — <b>cookies ignore the port</b>,
///         so a development build on <c>localhost:5099</c> breaks a container on <c>localhost:5081</c>.</item>
/// </list>
/// <para>
/// All three are most likely on a <b>first install</b>, which is the worst possible moment to hand
/// someone a blank 400. A token we cannot read carries no security value — it can neither prove nor
/// disprove anything — so dropping it costs nothing: the antiforgery middleware then sees a request with
/// no cookie and issues a new one, which is the path a first-time visitor already takes.
/// </para>
/// <para>
/// The signed-in cookie needs no equivalent handling: cookie authentication already treats an
/// undecryptable ticket as "not signed in" and redirects to the login page.
/// </para>
/// </summary>
public static class StaleAntiforgeryCookie
{
    /// <summary>ASP.NET Core names these <c>.AspNetCore.Antiforgery.&lt;hash&gt;</c> unless configured otherwise.</summary>
    private const string CookiePrefix = ".AspNetCore.Antiforgery.";

    /// <summary>
    /// The purpose string <c>DefaultAntiforgeryTokenSerializer</c> protects tokens with. If a future
    /// framework version changes it, <see cref="CanDecrypt"/> simply never reports a stale cookie and
    /// behaviour falls back to today's — the middleware fails closed to the status quo, never open.
    /// </summary>
    private const string TokenPurpose = "Microsoft.AspNetCore.Antiforgery.AntiforgeryToken.v1";

    public static IApplicationBuilder UseStaleAntiforgeryCookieRecovery(this WebApplication app)
    {
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(TokenPurpose);
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MT.Uptime.Antiforgery");

        return app.Use(async (context, next) =>
        {
            var stale = context.Request.Cookies
                .Where(c => c.Key.StartsWith(CookiePrefix, StringComparison.Ordinal))
                .Where(c => !CanDecrypt(protector, c.Value))
                .Select(c => c.Key)
                .ToList();

            if (stale.Count > 0)
            {
                foreach (var name in stale)
                    context.Response.Cookies.Delete(name);

                // Rewrite the request's own Cookie header too, and reset the parsed collection: deleting
                // only sets a Set-Cookie on the response, which does not stop the antiforgery middleware
                // from reading the stale value on *this* request and answering 400 anyway.
                var kept = context.Request.Cookies
                    .Where(c => !stale.Contains(c.Key))
                    .Select(c => $"{c.Key}={c.Value}")
                    .ToArray();

                if (kept.Length > 0)
                    context.Request.Headers.Cookie = string.Join("; ", kept);
                else
                    context.Request.Headers.Remove(HeaderNames.Cookie);

                // Constructed from the feature collection, not the parsed collection, so it re-reads the
                // header we just rewrote rather than caching the values we are trying to drop.
                context.Features.Set<IRequestCookiesFeature>(new RequestCookiesFeature(context.Features));

                log.LogInformation(
                    "Discarded {Count} antiforgery cookie(s) this instance cannot decrypt — the Data Protection " +
                    "key ring has changed since they were issued. A fresh token will be issued. This is normal " +
                    "after recreating the state directory or when another instance used the same hostname.",
                    stale.Count);
            }

            await next();
        });
    }

    private static bool CanDecrypt(IDataProtector protector, string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            protector.Unprotect(WebEncoders.Base64UrlDecode(value));
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }   // not even valid base64url
    }
}

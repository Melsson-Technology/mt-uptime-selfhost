using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using MT.Uptime.Web.Endpoints;
using MT.Uptime.Web.Services;

namespace MT.Uptime.Web.Security;

/// <summary>
/// Re-checks a signed-in account against the database for the lifetime of a Blazor circuit.
/// <para>
/// The cookie handler's <c>OnValidatePrincipal</c> closes the same hole for HTTP requests, but an
/// established interactive circuit runs over a WebSocket and issues no further HTTP requests — so on its
/// own it would leave a deleted or demoted user with a working dashboard until they closed the tab. That
/// matters more than it sounds: the pages that mutate state (<c>/users</c>, <c>/monitors</c>,
/// <c>/settings</c>) are interactive, so the circuit is exactly where the remaining privilege lives.
/// </para>
/// <para>
/// Returning false here tears the circuit's authentication state down; the router then re-evaluates every
/// <c>[Authorize]</c> gate and the user lands on the login page.
/// </para>
/// </summary>
internal sealed class RevalidatingUserAuthenticationState(
    ILoggerFactory loggerFactory,
    UserAccountService users)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <summary>
    /// How often an open circuit re-checks. Short enough that revocation is prompt, long enough that a
    /// dashboard left open overnight is not a query generator — and the check itself normally answers
    /// from <see cref="UserAccountService"/>'s stamp cache rather than the database.
    /// </summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true) return false;

        var idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var stampClaim = principal.FindFirst(AuthEndpoints.SessionStampClaim)?.Value;

        // Fail closed, matching OnValidatePrincipal: a principal we cannot identify is not revalidated.
        if (!int.TryParse(idClaim, out var userId) || !int.TryParse(stampClaim, out var stamp))
            return false;

        return await users.ValidateSessionAsync(userId, stamp, cancellationToken);
    }
}

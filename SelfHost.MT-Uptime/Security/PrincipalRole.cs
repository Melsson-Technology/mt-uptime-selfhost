using System.Security.Claims;
using MT.Uptime.Core.Domain;

namespace MT.Uptime.Web.Security;

/// <summary>
/// Reads the signed-in account's <see cref="UserRole"/> back out of its claims.
/// <para>
/// The role is written at sign-in as <c>ClaimTypes.Role</c> holding <c>UserRole.ToString()</c> (see
/// <c>AuthEndpoints</c>). Policies are the normal way to consume it — a page asks "is this caller an
/// Editor" and never learns which role satisfied that. This exists for the one case that needs the value
/// rather than a yes/no: the services that take an acting role so they can refuse on their own account
/// rather than trusting that the control was never rendered.
/// </para>
/// </summary>
public static class PrincipalRole
{
    /// <summary>
    /// The caller's role, or <see cref="UserRole.Viewer"/> when there is no usable claim.
    /// <para>
    /// Fails closed, and the fallback is not incidental: <c>Viewer = 0</c> was chosen precisely so that
    /// the value a row takes when something is missing is the one that grants nothing. An anonymous
    /// principal, a cookie predating roles, a hand-edited claim and a renamed enum member all land here,
    /// and all of them should cost someone an access request rather than hand out an instance.
    /// </para>
    /// </summary>
    public static UserRole RoleOrViewer(this ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated is not true) return UserRole.Viewer;

        var claim = principal.FindFirst(ClaimTypes.Role)?.Value;
        if (string.IsNullOrWhiteSpace(claim)) return UserRole.Viewer;

        // Matched against the member names, deliberately, rather than with Enum.TryParse.
        //
        // TryParse also accepts the underlying number: a claim of "2" parses to Admin, and Enum.IsDefined
        // then agrees it is a real member, so the obvious implementation hands out administration for a
        // two-character string. Nothing in this application ever writes that — the claim is
        // Role.ToString() — which is precisely the problem, because a value nobody writes is a value
        // nobody thinks to check.
        //
        // Ordinal, case-sensitive comparison for the same reason: we know exactly what we wrote, so any
        // laxer match can only ever succeed on something we did not.
        foreach (var candidate in Enum.GetValues<UserRole>())
            if (string.Equals(candidate.ToString(), claim, StringComparison.Ordinal))
                return candidate;

        return UserRole.Viewer;
    }
}

namespace MT.Uptime.Web.Security;

/// <summary>
/// Names of the authorization policies, so pages and endpoints reference a constant rather than a string
/// literal. A typo in <c>[Authorize(Policy = "…")]</c> does not fail loudly — ASP.NET Core throws only
/// when the policy is evaluated, which for a rarely-visited admin page can be long after deployment.
/// <para>
/// There is no <c>Viewer</c> policy: read access is "any authenticated user", which the fallback policy
/// in Program.cs already requires. A page needing only that carries a bare <c>[Authorize]</c>.
/// </para>
/// </summary>
public static class AuthPolicies
{
    /// <summary>Manage monitors, notification channels and status pages. Satisfied by Editor or Admin.</summary>
    public const string Editor = "RequireEditor";

    /// <summary>Manage accounts, instance settings, backup and export. Admin only.</summary>
    public const string Admin = "RequireAdmin";
}

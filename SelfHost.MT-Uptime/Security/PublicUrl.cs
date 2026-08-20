namespace MT.Uptime.Web.Security;

/// <summary>
/// The origin MT-Uptime puts into links it emails out.
/// <para>
/// Password-reset links used to be built from <c>Request.Host</c>, which the client controls. With
/// <c>AllowedHosts</c> at its default of <c>*</c>, an unauthenticated caller who knew an account's email
/// address could POST <c>/auth/forgot</c> with a forged <c>Host</c> and cause a genuine, correctly-signed
/// reset email — from the real sender, with real prose — whose only link pointed at a host they own and
/// carried a live single-use token. Nothing about the message would look wrong to the recipient.
/// </para>
/// <para>
/// The fix is to stop deriving the origin from the request at all. Configure <c>App:PublicBaseUrl</c>
/// (see <c>mt-uptime.env.example</c>) and it is used verbatim. Left unset, the request host is still used
/// so an unconfigured install keeps working — but that is logged as an error, because it is the state the
/// attack needs.
/// </para>
/// </summary>
public sealed class PublicUrl(string? configured, ILogger<PublicUrl> log)
{
    private readonly string? _configured = Normalise(configured);
    private int _warned;

    /// <summary>The configured origin, or null when the deployment has not declared one.</summary>
    public string? Configured => _configured;

    /// <summary>
    /// Origin to build an outbound link from — no trailing slash, so callers append an absolute path.
    /// </summary>
    public string Origin(HttpRequest request)
    {
        if (_configured is not null) return _configured;

        // Once per process: this fires on a path that already sends mail, and repeating it per request
        // would bury the delivery errors an operator is likely reading the log for.
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            log.LogError(
                "App:PublicBaseUrl is not set, so emailed links are built from the request's Host header. " +
                "A caller who forges that header can direct a password-reset link at a host they control. " +
                "Set App:PublicBaseUrl (e.g. https://uptime.example.com) — see mt-uptime.env.example.");
        }

        return $"{request.Scheme}://{request.Host}";
    }

    /// <summary>
    /// Host component of the configured origin, or null. Used to tighten <c>AllowedHosts</c> from its
    /// permissive default, so declaring the public URL also stops forged Host headers reaching a handler.
    /// </summary>
    public string? ConfiguredHost
        => _configured is not null && Uri.TryCreate(_configured, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;

    private static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimEnd('/');

        // Refuse anything that is not an absolute http(s) origin rather than silently emitting a broken
        // link — a malformed setting here would otherwise only surface in someone's inbox.
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? trimmed
                : null;
    }
}

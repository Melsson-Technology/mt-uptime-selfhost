namespace MT.Uptime.Core.Monitoring.Configs;

/// <summary>How an HTTP monitor authenticates to its target.</summary>
public enum HttpAuthMode
{
    None = 0,
    Basic = 1,
    Bearer = 2,
}

/// <summary>Type-specific settings for an HTTP/S monitor, serialized into <c>Monitor.ConfigJson</c>.</summary>
public sealed class HttpMonitorConfig
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";

    /// <summary>Accepted codes as comma-separated single values and/or ranges, e.g. "200-299,301".</summary>
    public string AcceptedStatusCodes { get; set; } = "200-299";

    /// <summary>Optional substring that must appear in the response body (case-insensitive).</summary>
    public string? Keyword { get; set; }

    /// <summary>When true, the check fails if the keyword IS present (instead of absent).</summary>
    public bool KeywordInverted { get; set; }

    public bool FollowRedirects { get; set; } = true;
    public bool IgnoreTlsErrors { get; set; }

    public HttpAuthMode AuthMode { get; set; } = HttpAuthMode.None;

    /// <summary>Username for <see cref="HttpAuthMode.Basic"/>. Not a secret, so stored in the clear.</summary>
    public string? AuthUsername { get; set; }

    /// <summary>
    /// Encrypted ciphertext of the Basic password or the Bearer token (never plaintext at rest).
    /// Written by the editor via <c>ISecretProtector.Protect</c>; read by <c>HttpChecker</c>.
    /// </summary>
    public string? AuthSecret { get; set; }

    /// <summary>
    /// Encrypted ciphertext of the custom request headers, one <c>Name: value</c> per line.
    /// <para>
    /// Encrypted rather than stored plainly because this is where API keys actually end up in practice
    /// (<c>X-API-Key</c>, <c>Authorization</c>, signed tokens); a field that usually holds a credential
    /// is treated as one. Unlike <see cref="AuthSecret"/> the editor decrypts and redisplays these,
    /// because most lines are not secret and have to stay editable.
    /// </para>
    /// </summary>
    public string? Headers { get; set; }

    /// <summary>Request body, sent for methods that carry one. Not encrypted.</summary>
    public string? Body { get; set; }

    /// <summary>Content-Type for <see cref="Body"/>. An explicit header line overrides this.</summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// Per-monitor User-Agent override. Blank keeps <c>HttpChecker.UserAgent</c>. Needed when a target's
    /// WAF allowlists a specific UA, which is otherwise an unmonitorable endpoint.
    /// </summary>
    public string? UserAgent { get; set; }

    public bool IsStatusAccepted(int code)
    {
        foreach (var part in AcceptedStatusCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash], out var lo) &&
                    int.TryParse(part[(dash + 1)..], out var hi) &&
                    code >= lo && code <= hi)
                    return true;
            }
            else if (int.TryParse(part, out var single) && code == single)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Splits decrypted header text into name/value pairs. Blank lines and <c>#</c> comments are skipped,
    /// as is any line without a colon — a malformed line is dropped rather than failing the whole check,
    /// which would take a monitor down over a typo in an optional field.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> ParseHeaders(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].TrimEnd();
            var value = line[(colon + 1)..].TrimStart();
            if (name.Length > 0) yield return (name, value);
        }
    }
}

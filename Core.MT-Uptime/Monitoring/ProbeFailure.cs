using System.Text;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Turns an exception from a probe into the sentence an operator reads in an alert.
/// <para>
/// Every checker used to report <c>ex.Message</c> alone, and for the failure operators meet most
/// often — a TLS handshake that will not complete — that message is a signpost rather than an
/// answer:
/// </para>
/// <list type="bullet">
/// <item><c>"The SSL connection could not be established, see inner exception."</c> — HTTPS</item>
/// <item><c>"SSL Authentication Error"</c> — MySQL</item>
/// </list>
/// <para>
/// Neither contains the words <em>certificate</em>, <em>expired</em>, <em>chain</em> or <em>name</em>.
/// The reason is one level down, in an <c>AuthenticationException</c> or a
/// <c>CryptographicException</c> that was being discarded. An operator paged at 3am was told that TLS
/// failed and nothing whatsoever about why — for the single commonest way a monitored endpoint
/// breaks.
/// </para>
/// <para>
/// This was found three times over by the end-to-end battery: once as a documented wart in the HTTPS
/// checker, once in MySQL, and once when it actively obstructed diagnosing a real failure on a real
/// box — the alert said <c>"SSL Authentication Error"</c> and the reason had already been thrown away
/// by the time anyone could look.
/// </para>
/// </summary>
public static class ProbeFailure
{
    /// <summary>
    /// How far down the inner-exception chain to walk. Three levels covers
    /// <c>HttpRequestException → AuthenticationException → CryptographicException</c>, which is the
    /// deepest of the shapes that matter; beyond that the text stops being a sentence and starts
    /// being a stack trace in prose.
    /// </summary>
    private const int MaxDepth = 3;

    /// <summary>
    /// The outer message, followed by each distinct inner message, joined with <c>" — "</c>.
    /// <para>
    /// Distinct is doing real work: several driver exceptions repeat the wrapped message verbatim,
    /// and <c>"X — X"</c> reads like a bug in the monitoring tool rather than a fault in the target.
    /// A message that adds nothing is dropped rather than appended.
    /// </para>
    /// <para>
    /// The result is not length-capped here — <see cref="CheckResult.Truncate"/> already does that for
    /// every message, and it is the right place for it, because the cap exists to stop a hostile
    /// target inflating an alert past what a notification channel will accept.
    /// </para>
    /// </summary>
    public static string Describe(Exception exception)
    {
        var text = new StringBuilder();
        var seen = new List<string>(MaxDepth + 1);

        for (var (ex, depth) = (exception, 0); ex is not null && depth <= MaxDepth; ex = ex.InnerException, depth++)
        {
            var message = ex.Message?.Trim();
            if (string.IsNullOrEmpty(message)) continue;

            // An inner message that merely repeats an outer one, or that is already contained in what
            // has been said, adds nothing an operator can act on.
            if (seen.Any(s => s.Contains(message, StringComparison.Ordinal))) continue;

            if (text.Length > 0) text.Append(" — ");
            text.Append(message);
            seen.Add(message);
        }

        // Some exceptions genuinely carry no message. Reporting the type is worse than nothing only
        // if the alternative is a real sentence, and here the alternative is an empty string.
        return text.Length > 0 ? text.ToString() : exception.GetType().Name;
    }
}

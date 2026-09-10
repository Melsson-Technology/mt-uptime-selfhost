using System.Security.Authentication;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// Covers <see cref="CheckResult.Down(Exception, double?, string?, DateTime?, bool)"/> — the overload
/// every checker's catch block now calls.
/// <para>
/// It deliberately owns no describing logic of its own: two implementations of "unwrap the inner chain"
/// briefly existed on separate branches, and one of them had to go, because the HTTP path and the
/// database paths formatting the same failure differently is a defect an operator sees and nobody
/// tests. <see cref="ProbeFailure.Describe"/> is the survivor; what is worth asserting here is that
/// this overload routes to it and still truncates.
/// </para>
/// </summary>
public class CheckResultDownFromExceptionTests
{
    [Fact]
    public void The_overload_describes_through_ProbeFailure_rather_than_its_own_walk()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"));

        var result = CheckResult.Down(ex, 1234.5);

        Assert.Equal(ProbeFailure.Describe(ex), result.Message);
        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal(1234.5, result.ResponseTimeMs);
    }

    /// <summary>
    /// The reason the overload exists at all: what an operator used to be handed was a sentence whose
    /// only content was an instruction to read something we were throwing away.
    /// </summary>
    [Fact]
    public void A_tls_failure_finally_says_the_word_certificate()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"));

        var message = CheckResult.Down(ex).Message;

        Assert.Contains("certificate", message);
        Assert.Contains("NotTimeValid", message);

        // And what the alert carried before this work.
        Assert.DoesNotContain("certificate", ex.Message);
    }

    [Fact]
    public void The_down_overload_truncates_like_every_other_message()
    {
        var described = CheckResult.Down(new InvalidOperationException(new string('x', 5000)));

        Assert.NotNull(described.Message);
        Assert.True(described.Message!.Length <= CheckResult.MaxMessageLength + 16);
        Assert.EndsWith("… (truncated)", described.Message);
    }

    [Fact]
    public void The_hard_flag_and_status_code_survive_the_overload()
    {
        var result = CheckResult.Down(new InvalidOperationException("nope"), 12.0, "521", hard: true);

        Assert.True(result.Hard);
        Assert.Equal("521", result.StatusCode);
    }
}

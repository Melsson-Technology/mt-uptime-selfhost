using System.Security.Authentication;
using System.Security.Cryptography;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// <see cref="ProbeFailure.Describe"/> is what an operator actually reads at 3am, so the cases that
/// matter here are the ugly ones: a driver that repeats itself, an exception with no message, and a
/// chain deep enough that continuing to walk it would produce a stack trace in prose.
/// </summary>
public class ProbeFailureTests
{
    [Fact]
    public void A_lone_exception_describes_as_its_own_message()
        => Assert.Equal("Connection refused", ProbeFailure.Describe(new IOException("Connection refused")));

    [Fact]
    public void The_inner_reason_is_appended_to_the_signpost()
    {
        // The real shape, and the whole reason this class exists: the outer message names the
        // subsystem and the inner one names the fault.
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate is invalid because of errors in the certificate chain."));

        Assert.Equal(
            "The SSL connection could not be established, see inner exception. — "
            + "The remote certificate is invalid because of errors in the certificate chain.",
            ProbeFailure.Describe(ex));
    }

    [Fact]
    public void Three_levels_are_joined_in_order()
    {
        var ex = new HttpRequestException("outer",
            new AuthenticationException("middle",
                new CryptographicException("inner")));

        Assert.Equal("outer — middle — inner", ProbeFailure.Describe(ex));
    }

    [Fact]
    public void A_fourth_level_is_not_walked()
    {
        // MaxDepth exists because past three levels the text stops being a sentence. Prove the cap
        // by putting something recognisable just beyond it.
        var ex = new Exception("one",
            new Exception("two",
                new Exception("three",
                    new Exception("four",
                        new Exception("five")))));

        var text = ProbeFailure.Describe(ex);

        Assert.Equal("one — two — three — four", text);
        Assert.DoesNotContain("five", text);
    }

    [Fact]
    public void A_repeated_message_is_said_once()
    {
        // Several database drivers wrap their inner exception and re-use its message verbatim.
        // "X — X" reads like a bug in the monitoring tool rather than a fault in the target.
        var ex = new Exception("SSL Authentication Error", new Exception("SSL Authentication Error"));

        Assert.Equal("SSL Authentication Error", ProbeFailure.Describe(ex));
    }

    [Fact]
    public void An_inner_message_already_contained_in_the_outer_one_is_dropped()
    {
        var ex = new Exception("Authentication failed: bad certificate", new Exception("bad certificate"));

        Assert.Equal("Authentication failed: bad certificate", ProbeFailure.Describe(ex));
    }

    [Fact]
    public void A_wider_inner_message_is_kept_even_though_it_contains_the_outer_one()
    {
        // Containment is checked one way round on purpose: the inner message here says strictly
        // more, and dropping it would discard the only useful half.
        var ex = new Exception("timeout", new Exception("timeout after 30s awaiting the TLS handshake"));

        Assert.Equal("timeout — timeout after 30s awaiting the TLS handshake", ProbeFailure.Describe(ex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_link_in_the_chain_is_skipped_rather_than_ending_the_walk(string blank)
    {
        var ex = new Exception("outer", new Exception(blank, new Exception("the actual reason")));

        Assert.Equal("outer — the actual reason", ProbeFailure.Describe(ex));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
        => Assert.Equal("Connection refused", ProbeFailure.Describe(new Exception("  Connection refused  ")));

    [Fact]
    public void An_exception_with_no_usable_message_falls_back_to_its_type_name()
    {
        // Not hypothetical: OperationCanceledException and friends are routinely thrown bare, and a
        // notification whose body is the empty string tells an operator nothing at all.
        var text = ProbeFailure.Describe(new BlankException());

        Assert.Equal(nameof(BlankException), text);
    }

    private sealed class BlankException : Exception
    {
        public override string Message => string.Empty;
    }
}

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Tests;

/// <summary>
/// A secret the key ring cannot decrypt has to be said out loud, once. Every caller swallows the failure
/// and treats the secret as absent — right for one alert, and it made a database restored without its
/// keys indistinguishable from a healthy one: every notification failed, the channel editor said "check
/// the logs", and the logs were empty. That was measured on a clean box, not reasoned about.
/// </summary>
public class SecretProtectorTests
{
    [Fact]
    public void A_secret_from_its_own_key_ring_round_trips_without_a_word()
    {
        var log = new CapturingLogger();
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider(), log);

        Assert.Equal("s3cret", protector.Unprotect(protector.Protect("s3cret")));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void A_secret_from_another_key_ring_is_logged_once_and_still_throws()
    {
        // Two ephemeral providers are two key rings: the second has never seen the key the first used,
        // which is exactly the state of an instance restored from a database without its keys/.
        const string plaintext = "https://hooks.example.com/services/T0/B0/do-not-log-me";
        var cipher = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider(), new CapturingLogger())
            .Protect(plaintext);

        var log = new CapturingLogger();
        var here = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider(), log);

        // The callers rely on the exception to know the secret is unusable, so logging must not eat it.
        Assert.ThrowsAny<CryptographicException>(() => here.Unprotect(cipher));
        Assert.ThrowsAny<CryptographicException>(() => here.Unprotect(cipher));

        // Once, however many checks and alerts go on to hit the same missing key.
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("could not be decrypted", entry.Message, StringComparison.Ordinal);
        Assert.Contains("keys/", entry.Message, StringComparison.Ordinal);

        // Neither the secret nor its ciphertext belongs in a journal.
        Assert.DoesNotContain("do-not-log-me", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(cipher, entry.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<DataProtectionSecretProtector>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

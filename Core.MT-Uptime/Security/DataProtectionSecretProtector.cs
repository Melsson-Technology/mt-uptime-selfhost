using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace MT.Uptime.Core.Security;

/// <summary>
/// Encrypts secrets with an <see cref="IDataProtector"/>. The keys are persisted to disk and backed up
/// with the database (see Program.cs), so secrets survive restarts.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionSecretProtector> _log;

    // One line per distinct cause rather than one per attempt. A key ring that has lost a key fails every
    // check and every alert that needs it, and a journal of identical errors buries the first one.
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    public DataProtectionSecretProtector(IDataProtectionProvider provider, ILogger<DataProtectionSecretProtector> log)
    {
        _protector = provider.CreateProtector("MT.Uptime.Secrets.v1");
        _log = log;
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext)
    {
        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            // Every caller treats a secret it cannot read as absent, which is the right thing for the
            // caller and was invisible to the operator: a database restored without its key ring started,
            // reported healthy, and failed every alert with nothing in the log while the UI said "check
            // the logs". The exception names the missing key id, never the plaintext, so it is safe to log.
            if (_reported.TryAdd(ex.Message, 0))
            {
                _log.LogError(
                    "A stored secret could not be decrypted: {Reason} Notification channels, monitored-database " +
                    "passwords and HTTP credentials encrypted with that key are unusable until the key ring that " +
                    "encrypted them is back beside the database. This is what a database restored or copied " +
                    "without its keys/ directory looks like.",
                    ex.Message);
            }
            throw;
        }
    }
}

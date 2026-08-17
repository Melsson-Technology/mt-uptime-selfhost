using Microsoft.AspNetCore.DataProtection;

namespace MT.Uptime.Core.Security;

/// <summary>
/// Encrypts secrets with an <see cref="IDataProtector"/>. The keys are persisted to disk and backed up
/// with the database (see Program.cs), so secrets survive restarts.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("MT.Uptime.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}

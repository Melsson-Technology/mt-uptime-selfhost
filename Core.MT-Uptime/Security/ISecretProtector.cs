namespace MT.Uptime.Core.Security;

/// <summary>Encrypts/decrypts small secrets (API keys, DB passwords) at rest via the Data Protection API.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

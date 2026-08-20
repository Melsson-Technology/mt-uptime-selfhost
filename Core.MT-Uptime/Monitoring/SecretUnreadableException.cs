namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Thrown when a monitor's stored credential cannot be decrypted with the current key ring.
/// <para>
/// Every checker that reads a secret raises this rather than probing with a blank one, so the Down
/// reason says whose fault it is; each catches it as a <em>hard</em> Down, since retrying cannot bring
/// the keys back and burning the retry cushion only delays the alert.
/// </para>
/// </summary>
public sealed class SecretUnreadableException(string message, Exception inner) : Exception(message, inner);

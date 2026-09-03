using Microsoft.Extensions.DependencyInjection;
using MT.Uptime.Core;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Tests.E2E.Support;

/// <summary>
/// The real checkers, resolved from the real container, for the Tier 1 matrix.
/// <para>
/// Not <c>new HttpChecker(...)</c>. That checker takes an <see cref="IHttpClientFactory"/> and asks it
/// for one of four named clients (<c>monitor</c>, <c>monitor-noredirect</c>, <c>monitor-insecure</c>,
/// <c>monitor-insecure-noredirect</c>), each configured by <c>AddMonitoringEngine</c> with a specific
/// <c>AllowAutoRedirect</c>, a specific certificate-validation callback, and the product's
/// User-Agent. <c>CreateClient</c> with an unregistered name does not throw — it returns a plain client
/// that follows redirects and validates certificates — so a hand-rolled factory would silently test
/// something the product never uses, in the least safe direction.
/// </para>
/// <para>
/// Building the container instead means the redirect and TLS toggles under test are the product's own
/// registrations. This is the same shape <c>Tests.MT-Uptime/WebhookLoggingTests</c> uses to prove every
/// checker can be constructed at all.
/// </para>
/// </summary>
public sealed class CheckerHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IReadOnlyDictionary<MonitorType, IMonitorChecker> _checkers;

    public CheckerHost()
    {
        var services = new ServiceCollection();

        // Mandatory: the probe clients keep the default logging handler, which resolves ILoggerFactory.
        services.AddLogging();

        // AddMonitoringEngine registers ISecretProtector with a plain AddSingleton, so a later
        // registration wins for constructor injection. That is deliberately exploited here.
        services.AddMonitoringEngine();

        // Identity "encryption", registered AFTER the engine so it overrides
        // DataProtectionSecretProtector. Tier 1 is about what the checkers do with a credential, not
        // about Data Protection — and the real provider would want a key ring on disk and would log a
        // "no XML encryptor configured" warning on every run. Tier 2 uses the real one, because there
        // the ciphertext is genuinely in a database.
        services.AddSingleton<ISecretProtector, PassthroughProtector>();

        _provider = services.BuildServiceProvider();

        // No IDbContextFactory is registered, and none is needed: nothing on a checker's constructor
        // path touches the database. If that ever changes, this line is where it will surface.
        _checkers = _provider.GetServices<IMonitorChecker>().ToDictionary(c => c.Type);
    }

    /// <summary>Every actively-probed monitor type. Push has no checker — nothing reaches out for it.</summary>
    public IReadOnlyDictionary<MonitorType, IMonitorChecker> Checkers => _checkers;

    public IMonitorChecker For(MonitorType type) =>
        _checkers.TryGetValue(type, out var checker)
            ? checker
            : throw new InvalidOperationException(
                $"No checker is registered for {type}. AddMonitoringEngine registers one per actively-"
                + "probed type; Push is passive and correctly has none.");

    public ISecretProtector Protector => _provider.GetRequiredService<ISecretProtector>();

    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// Identity "encryption". Re-declared here rather than reused because the original is a private nested
/// class inside <c>Tests.MT-Uptime/HttpCheckerTests</c>, and <c>InternalsVisibleTo</c> on Core names
/// <c>MT.Uptime.Tests</c> — not this assembly.
/// </summary>
public sealed class PassthroughProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string ciphertext) => ciphertext;
}

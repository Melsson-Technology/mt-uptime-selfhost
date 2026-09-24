using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MT.Uptime.Tests;

/// <summary>
/// The engine's appsettings files, read the way the host reads them.
/// <para>
/// They carry comment keys at the root, an idiom with a sharp edge: a comment key placed one level
/// deeper, inside <c>Logging:LogLevel</c>, is read as a log category whose value must parse as a
/// <see cref="LogLevel"/>, and it stopped a sibling application at startup before it bound a port.
/// These tests are what would catch that here, along with the one setting the comment explains.
/// </para>
/// </summary>
public sealed class AppSettingsTests
{
    private static string SelfHostDir([CallerFilePath] string thisFile = "")
    {
        var dir = Directory.GetParent(thisFile);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MT-Uptime.Engine.slnx")))
            dir = dir.Parent;

        // A failure, not a skip: a test that passes because it found nothing to check has checked nothing.
        Assert.NotNull(dir);
        var selfHost = Path.Combine(dir!.FullName, "SelfHost.MT-Uptime");
        Assert.True(Directory.Exists(selfHost), $"SelfHost project not found at {selfHost}");
        return selfHost;
    }

    private static string[] SettingsFiles()
    {
        var files = Directory.GetFiles(SelfHostDir(), "appsettings*.json").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        return files;
    }

    [Fact]
    public void Every_settings_file_is_strict_json()
    {
        // No comments, no trailing commas. "It looked like JSON" is how a comment ends up somewhere
        // load-bearing, and the root-level "//" key is the supported way to leave one.
        foreach (var file in SettingsFiles())
            Assert.Null(Record.Exception(() => JsonDocument.Parse(File.ReadAllText(file))));
    }

    [Fact]
    public void Every_log_level_is_a_real_LogLevel_and_the_host_can_build_its_filters()
    {
        foreach (var file in SettingsFiles())
        {
            var config = new ConfigurationBuilder().AddJsonFile(file, optional: false).Build();

            foreach (var entry in config.GetSection("Logging:LogLevel").GetChildren())
            {
                Assert.True(Enum.TryParse<LogLevel>(entry.Value, ignoreCase: true, out _),
                    $"{Path.GetFileName(file)}: Logging:LogLevel:{entry.Key} is \"{entry.Value}\", not a LogLevel. " +
                    "Every key in that object is a log category; comments belong at the root of the file.");
            }

            // Resolving the factory is what materialises the filter rules, which is where a bad value throws.
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddConfiguration(config.GetSection("Logging")));
            using var provider = services.BuildServiceProvider();
            Assert.Null(Record.Exception(() =>
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("probe").IsEnabled(LogLevel.Information)));
        }
    }

    [Fact]
    public void Production_does_not_log_every_SQL_statement()
    {
        // At Information, EF Core writes every statement it executes: about 3 KB of log per check,
        // measured, which under Docker's unrotated json-file driver is a disk that fills.
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SelfHostDir(), "appsettings.json"), optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConfiguration(config.GetSection("Logging")).AddProvider(new NullProvider()));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        Assert.False(factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Warning));

        // The engine's own logging is not collateral: it is what an operator reads after an incident.
        Assert.True(factory.CreateLogger("MT.Uptime.Core.Monitoring.RetentionService").IsEnabled(LogLevel.Information));
    }

    /// <summary>IsEnabled answers false with no provider at all, which would pass the assertion for free.</summary>
    private sealed class NullProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
        public void Dispose() { }

        private sealed class NullLogger : ILogger
        {
            public static readonly NullLogger Instance = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }
        }
    }
}

using System.Diagnostics;
using System.Text;

namespace MT.Uptime.Tests.E2E.Support;

/// <summary>Every target the break/restore helper accepts. Spelled as an enum for the same reason
/// <see cref="Targets"/>'s accessors are spelled out: a mistyped string is ~100 runtime failures,
/// a mistyped enum member is one compile error.</summary>
public enum Target
{
    /// <summary>The HTTP fixture returns 503 from every route. A <b>hard</b> Down for HTTP monitors.</summary>
    Http,

    /// <summary>The fixture still returns 200, but slowly. This is how Degraded is provoked.</summary>
    HttpSlow,

    /// <summary>The TCP listener stops, so the port refuses. A <b>soft</b> Down.</summary>
    Tcp,

    /// <summary>dnsmasq stops answering, so the zone stops resolving.</summary>
    Dns,

    /// <summary>mysqld is stopped.</summary>
    MySql,

    /// <summary>postgresql is stopped.</summary>
    Postgres,

    /// <summary>
    /// Everything except <see cref="HttpSlow"/>. The helper skips it deliberately: the two HTTP breaks
    /// are mutually exclusive, and its health predicate reads the down flag before the slow one, so a
    /// combined break could never satisfy its own wait.
    /// </summary>
    All,
}

/// <summary>
/// Breaks and restores an E2E target service through the root-owned helper at
/// <c>/usr/local/bin/mt-uptime-e2e-target</c>, reached with <c>sudo -n</c>.
/// <para>
/// <b>The helper blocks until the change is observable from outside</b> — the port really refuses,
/// the fixture really returns 503 — or fails after its own 60-second cap. That is why nothing here
/// polls: <c>systemctl stop mysql</c> returns before the socket has finished closing, and a test that
/// broke a target and immediately started asserting Down would be racing the thing it just asked for
/// and would occasionally catch one last healthy check. Because the wait lives in the helper, every
/// test's timeout budget covers only what it is actually measuring: the monitor noticing.
/// </para>
/// <para>
/// <b>Always <c>sudo -n</c>.</b> A sudo that can prompt is a test run that hangs until the CI timeout
/// with no output explaining why. Non-interactive turns a missing sudoers rule into an immediate,
/// legible failure instead.
/// </para>
/// </summary>
public static class TargetControl
{
    /// <summary>
    /// How long to wait on the helper itself. Its own cap is 60 s per target, and <see cref="Target.All"/>
    /// walks five of them in sequence, so this has to clear 300 s to avoid killing a helper that was
    /// about to succeed — a truncated break leaves the box in a state the next test blames itself for.
    /// </summary>
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(330);

    /// <summary>The helper's path, from the manifest so it tracks the installer rather than a literal here.</summary>
    public static string HelperPath => Targets.Helper;

    /// <summary>
    /// Breaks <paramref name="target"/> and returns a handle that restores it on dispose.
    /// <para>
    /// Always <c>using</c> this. A test that breaks a target and then fails an assertion would
    /// otherwise leave it broken for every test after it, and the resulting cascade points at the
    /// wrong scenario entirely — the second failure is louder than the first and is not the cause.
    /// </para>
    /// </summary>
    public static Broken Break(Target target)
    {
        Run("break", target);
        return new Broken(target);
    }

    /// <summary>Restores a target. Idempotent — restoring something already healthy is a no-op that succeeds.</summary>
    public static void Restore(Target target) => Run("restore", target);

    /// <summary>
    /// The helper's own view of every target, for a failure message. Never throws: it is only ever
    /// called when something has already gone wrong, and a diagnostic that throws hides the fault it
    /// was printed to explain.
    /// </summary>
    public static string Status()
    {
        try
        {
            var (exitCode, output) = Execute("status", "all");
            return exitCode == 0 ? output : $"(some targets are down)\n{output}";
        }
        catch (Exception e)
        {
            return $"(could not read target status: {e.Message})";
        }
    }

    private static void Run(string verb, Target target)
    {
        var name = CliName(target);
        var (exitCode, output) = Execute(verb, name);
        if (exitCode == 0) return;

        throw new InvalidOperationException(
            $"'sudo -n {HelperPath} {verb} {name}' failed with exit code {exitCode}.\n{output}\n"
            + Advice(exitCode, output));
    }

    private static (int ExitCode, string Output) Execute(string verb, string target)
    {
        var psi = new ProcessStartInfo("sudo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Argument-by-argument, never a joined command line: these values reach a sudoers rule that
        // matches on the exact argument vector, and quoting them into one string would both break that
        // match and reintroduce the injection the helper's closed `case` exists to prevent.
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(HelperPath);
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(target);

        // The helper reads the manifest itself. Passing ours through means a test run pointed at a
        // non-default manifest does not silently break against the default one — except that sudo
        // strips the environment unless the sudoers rule keeps it, so this is a best-effort courtesy
        // for the sudo-less case rather than something to rely on.
        psi.Environment[Targets.PathVariable] = Targets.ManifestPath;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start sudo — is it installed?");

        // Both streams are read asynchronously before waiting. Reading one to the end and then the
        // other deadlocks the moment a child fills the pipe it is not being drained on, and the helper
        // writes to both.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)HelperTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* it may have exited by now */ }
            throw new TimeoutException(
                $"'{HelperPath} {verb} {target}' did not finish within {HelperTimeout.TotalSeconds:0}s. "
                + "The helper caps its own wait at 60s per target, so this means it hung rather than "
                + "gave up — check whether the service it manages is stuck in systemd.");
        }

        var output = new StringBuilder();
        output.Append(stdout.GetAwaiter().GetResult());
        var errors = stderr.GetAwaiter().GetResult();
        if (errors.Length > 0) output.Append(errors);

        return (process.ExitCode, output.ToString().TrimEnd());
    }

    /// <summary>
    /// Turns the two failures that actually happen into instructions. Both are configuration, both
    /// look like a broken test, and both cost an hour the first time.
    /// </summary>
    private static string Advice(int exitCode, string output)
    {
        if (output.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no tty present", StringComparison.OrdinalIgnoreCase)
            || exitCode == 1 && output.Contains("sudo:", StringComparison.Ordinal))
        {
            return $"""
                    sudo refused without a password, so the NOPASSWD rule is not in effect for this user.
                    The rule is installed by install-targets.sh for E2E_TEST_USER (currently
                    '{Targets.Optional("E2E_TEST_USER") ?? "unset"}'), and you are running as
                    '{Environment.UserName}'. Either run the tests as that account, or re-run:
                        sudo ./e2e/install-targets.sh --only helper
                    with E2E_TEST_USER set to the account you intend to use.
                    """;
        }

        return "Current target status:\n" + Status();
    }

    /// <summary>The exact argument the helper and its sudoers rule expect for this target.</summary>
    public static string CliName(Target target) => target switch
    {
        Target.Http => "http",
        Target.HttpSlow => "http-slow",
        Target.Tcp => "tcp",
        Target.Dns => "dns",
        Target.MySql => "mysql",
        Target.Postgres => "postgres",
        Target.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "no CLI name for this target"),
    };

    /// <summary>
    /// A broken target, restored when disposed.
    /// <para>
    /// Restore failures are swallowed <em>only</em> when a test is already failing, which Dispose
    /// cannot know — so they are not swallowed at all. Throwing from Dispose while an assertion is
    /// unwinding would replace the real failure with this one, so the restore error is written to the
    /// test output instead, where it is visible without being mistaken for the cause.
    /// </para>
    /// </summary>
    public sealed class Broken(Target target) : IDisposable
    {
        private bool _restored;

        public Target Target { get; } = target;

        /// <summary>
        /// Restores early, inside the test, so the assertions after it can watch the recovery. Dispose
        /// then does nothing. This is the normal shape of an Up → Down → Up scenario; the dispose path
        /// is the safety net for the failing case.
        /// </summary>
        public void RestoreNow()
        {
            if (_restored) return;
            Restore(Target);
            _restored = true;
        }

        public void Dispose()
        {
            if (_restored) return;
            try
            {
                Restore(Target);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(
                    $"WARNING: could not restore '{CliName(Target)}' after the test. Every later test "
                    + $"that touches it will fail until it is restored by hand:\n"
                    + $"    sudo {HelperPath} restore {CliName(Target)}\n{e.Message}");
            }
            finally
            {
                _restored = true;
            }
        }
    }
}

using Xunit.Sdk;

namespace MT.Uptime.Tests.E2E.Support;

/// <summary>
/// A fact that needs the E2E target services, and skips itself when they are not present.
/// <para>
/// The <c>Skip</c> is set in the CONSTRUCTOR, which is not a stylistic choice. xUnit 2.x evaluates
/// <c>Skip</c> once, at discovery, when it constructs the attribute — there is no <c>Assert.Skip</c>
/// until xUnit v3, and this repository is on 2.9.3. So the decision has to be made here, and it has to
/// be made without throwing: a discovery-time exception is reported as a failure, not a skip, and the
/// battery's acceptance criterion is that a machine with no manifest reports every test skipped and
/// none failed.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!Targets.Available) Skip = Targets.SkipReason;
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="E2EFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (!Targets.Available) Skip = Targets.SkipReason;
    }
}

/// <summary>
/// A fact for the Playwright tier. Needs everything <see cref="E2EFactAttribute"/> does, plus an
/// installed MT-Uptime whose origin and admin credentials smoke.sh has appended to the manifest.
/// <para>
/// Kept distinct because the two halves of the battery fail independently: a box can have every target
/// service configured and no application deployed yet, and in that state the checker and pipeline
/// tiers should run while the UI tier steps aside.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UIFactAttribute : FactAttribute
{
    public UIFactAttribute()
    {
        if (UiSkip.Reason is { } reason) Skip = reason;
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="UIFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UITheoryAttribute : TheoryAttribute
{
    public UITheoryAttribute()
    {
        if (UiSkip.Reason is { } reason) Skip = reason;
    }
}

/// <summary>
/// The one place the UI tier's precondition is expressed, so the fact and theory attributes cannot
/// drift apart — a pair that disagreed would run half a tier on a box that could not support it.
/// </summary>
internal static class UiSkip
{
    public static string? Reason =>
        !Targets.Available ? Targets.SkipReason
        : !Targets.UiReady
            ? "The manifest has no MTU_BASE_URL/MTU_ADMIN_PASSWORD. Deploy MT-Uptime and run "
              + "./e2e/smoke.sh, which completes first-run setup and records them."
        : null;
}

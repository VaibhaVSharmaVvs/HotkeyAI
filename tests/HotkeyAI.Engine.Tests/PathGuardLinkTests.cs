using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The path guard must check where a path really goes, not only what it says.
/// </summary>
/// <remarks>
/// Containment used to be purely lexical, so a directory junction
/// created inside the allowed root — which needs no elevation — reached anywhere on the machine
/// while every path through it still read as being under the root. Reproduced live against a
/// junction to System32.
/// <para>
/// Tested through a fake resolver rather than a real junction, deliberately: these run on Linux
/// CI alongside the rest of the safety controls, and a test that needs <c>mklink</c> would not.
/// The Windows implementation of the seam is <c>WindowsRealPath</c>.
/// </para>
/// </remarks>
public sealed class PathGuardLinkTests
{
    private static readonly string[] Roots = [@"C:\Users\test\Projects"];

    /// <summary>Answers with whatever it was told, for the one path it was told about.</summary>
    private sealed class Links(string from, string? to) : IRealPath
    {
        public string? Resolve(string path) =>
            string.Equals(path, from, StringComparison.OrdinalIgnoreCase) ? to : null;
    }

    [Fact]
    public void APathThatLooksContainedButLeadsOutIsRefused()
    {
        var guard = new PathGuard(
            Roots,
            new Links(@"C:\Users\test\Projects\junction\cmd.exe", @"C:\Windows\System32\cmd.exe"));

        Assert.False(guard.IsAllowed(@"C:\Users\test\Projects\junction\cmd.exe", out var reason));

        // The refusal has to name the real destination. "Outside the allowed roots" for a path
        // that plainly is inside them would read as a bug in the guard rather than a warning.
        Assert.Contains(@"C:\Windows\System32\cmd.exe", reason, StringComparison.Ordinal);
        Assert.Contains("link", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APathThatResolvesInsideTheRootIsStillAllowed()
    {
        // Junctions inside the root pointing elsewhere inside it are ordinary and must keep
        // working.
        var guard = new PathGuard(
            Roots,
            new Links(@"C:\Users\test\Projects\link", @"C:\Users\test\Projects\real"));

        Assert.True(guard.IsAllowed(@"C:\Users\test\Projects\link", out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void AnUnresolvablePathKeepsItsLexicalVerdict()
    {
        // Null from the resolver means "no opinion" — a path that does not exist yet, or one this
        // process cannot open. Treating that as a refusal would break every plan naming a file it
        // is about to create.
        var guard = new PathGuard(Roots, new Links("nothing", null));

        Assert.True(guard.IsAllowed(@"C:\Users\test\Projects\not-there-yet.txt", out _));
    }

    [Fact]
    public void ResolutionCannotRescueAPathTheLexicalRuleRejects()
    {
        // Order matters: the cheap rule runs first and is final. A resolver that claimed an
        // out-of-root path really lands inside must not be able to widen the allowlist.
        var guard = new PathGuard(
            Roots, new Links(@"C:\Windows\System32", @"C:\Users\test\Projects\safe"));

        Assert.False(guard.IsAllowed(@"C:\Windows\System32", out var reason));
        Assert.Contains("outside the allowed roots", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoResolverTheGuardBehavesExactlyAsBefore()
    {
        // Every existing test constructs the guard without one, and the engine's own tests must
        // not start depending on a filesystem.
        var guard = new PathGuard(Roots);

        Assert.True(guard.IsAllowed(@"C:\Users\test\Projects\junction\cmd.exe", out _));
        Assert.False(guard.IsAllowed(@"C:\Windows\System32\cmd.exe", out _));
    }
}

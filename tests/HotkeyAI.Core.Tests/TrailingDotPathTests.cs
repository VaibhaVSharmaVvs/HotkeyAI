using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// A path means what Windows will make of it, not what the string looks like.
/// </summary>
/// <remarks>
/// Win32 strips trailing dots and spaces from a path component before acting on it, and <see
/// cref="WindowsPath"/> did not — so the two disagreed about what a path meant.
/// <c>Extension("pwn.bat.")</c> returned null and <c>Extension("pwn.bat ")</c> returned <c>".bat
/// "</c>, neither of which is in the executable blocklist, while Windows ran <c>pwn.bat</c> either
/// way.
/// <para>
/// That defeated the executable blocklist outright, and the trailing-space spelling also defeated
/// the preview's honesty: it renders <c>Open …\pwn.bat  with its default application</c> — note the
/// double space, which is the whole problem, because a trailing space is invisible to whoever is
/// approving it.
/// </para>
/// <para>
/// Verified against the live filesystem before fixing: <c>target.txt.</c>, <c>target.txt </c> and
/// <c>target.txt...</c> all open <c>target.txt</c>. Also checked, because it would have been a
/// worse bug in the containment check rather than the blocklist: <c>.. </c> does <em>not</em>
/// become a parent reference — Windows treats it as an ordinary name — so no trailing-space
/// spelling can climb out of an allowed root.
/// </para>
/// </remarks>
public sealed class TrailingDotPathTests
{
    // ------------------------------- the extension -------------------------------

    [Theory]
    [InlineData(@"C:\x\pwn.bat", ".bat")]
    [InlineData(@"C:\x\pwn.bat.", ".bat")]
    [InlineData(@"C:\x\pwn.bat ", ".bat")]
    [InlineData(@"C:\x\pwn.bat...", ".bat")]
    [InlineData(@"C:\x\pwn.bat   ", ".bat")]
    [InlineData(@"C:\x\pwn.bat. .", ".bat")]
    [InlineData(@"C:\x\pwn.exe.", ".exe")]
    public void TrailingDotsAndSpacesDoNotHideAnExtension(string path, string expected)
    {
        Assert.Equal(expected, WindowsPath.Extension(path));
    }

    [Theory]
    [InlineData(@"C:\x\notes", null)]
    [InlineData(@"C:\x\notes.", null)]
    [InlineData(@"C:\x\notes ", null)]
    [InlineData(@"C:\x\.gitconfig", null)]
    public void SomethingWithNoRealExtensionStillHasNone(string path, string? expected)
    {
        Assert.Equal(expected, WindowsPath.Extension(path));
    }

    [Theory]
    [InlineData(@"C:\x\target.txt.", "target.txt")]
    [InlineData(@"C:\x\target.txt ", "target.txt")]
    [InlineData(@"C:\folder.\target.txt", "target.txt")]
    public void TheFileNameIsTheEffectiveName(string path, string expected)
    {
        Assert.Equal(expected, WindowsPath.FileName(path));
    }

    // ------------------------------- containment -------------------------------

    [Fact]
    public void ATrailingDotOnADirectoryNoLongerCausesAFalseRefusal()
    {
        // Windows resolves C:\Test.\x to C:\Test\x, so refusing it was wrong — a usability wart
        // rather than a hole, but the guard should agree with the filesystem in both directions.
        Assert.True(WindowsPath.IsUnder(@"C:\Test.\x", @"C:\Test"));
        Assert.True(WindowsPath.IsUnder(@"C:\Test\x.", @"C:\Test"));
    }

    [Fact]
    public void TrailingWhitespaceCannotSmuggleAPathOutOfItsRoot()
    {
        // The direction that would actually matter. Nothing about trimming trailing dots or spaces
        // can move a component into a different parent.
        Assert.False(WindowsPath.IsUnder(@"C:\Other\x", @"C:\Test"));
        Assert.False(WindowsPath.IsUnder(@"C:\Other.\x", @"C:\Test"));
        Assert.False(WindowsPath.IsUnder(@"C:\Test-Other\x", @"C:\Test"));
    }

    [Fact]
    public void DotAndDotDotKeepTheirMeanings()
    {
        // The fix must not trim "." or ".." to nothing: traversal detection depends on recognising
        // them, and breaking that would be far worse than the bug being fixed.
        Assert.Null(WindowsPath.Normalise(@"C:\..\..\Windows"));
        Assert.False(WindowsPath.IsUnder(@"C:\Test\..\Windows\system32", @"C:\Test"));
        Assert.True(WindowsPath.IsUnder(@"C:\Test\sub\..\x", @"C:\Test"));
        Assert.True(WindowsPath.IsUnder(@"C:\Test\.\x", @"C:\Test"));
    }

    [Fact]
    public void ASegmentOfNothingButDotsIsLeftAlone()
    {
        // "..." is not a traversal and is not a name Windows resolves either; trimming it to empty
        // would silently change which directory a path refers to.
        Assert.Equal("...", WindowsPath.FileName(@"C:\x\..."));
    }

    // ------------------------------- the blocklist -------------------------------

    [Theory]
    [InlineData(@"C:\x\pwn.bat")]
    [InlineData(@"C:\x\pwn.bat.")]
    [InlineData(@"C:\x\pwn.bat ")]
    [InlineData(@"C:\x\pwn.bat...")]
    [InlineData(@"C:\x\pwn.bat. .")]
    [InlineData(@"C:\x\setup.exe.")]
    [InlineData(@"C:\x\dropper.ps1 ")]
    [InlineData(@"C:\x\shortcut.lnk.")]
    public void EverySpellingOfAnExecutableIsRefused(string path)
    {
        Assert.False(ShellOpen.IsAllowed(path, out var reason));
        Assert.Contains("Windows executes rather than opens", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\x\invoice.pdf.")]
    [InlineData(@"C:\x\notes.txt ")]
    [InlineData(@"C:\x\Projects.")]
    public void AnOrdinaryFileIsStillFineWhateverItsSpelling(string path)
    {
        Assert.True(ShellOpen.IsAllowed(path, out _));
    }

    [Fact]
    public void TheValidatorRefusesTheTrailingDotSpellingToo()
    {
        // Both layers share WindowsPath.Extension, so one fix closes both — pinned because that
        // sharing is the reason the fix is small, and someone could easily unpick it.
        foreach (var spelling in new[] { "pwn.bat", "pwn.bat.", "pwn.bat ", "pwn.bat..." })
        {
            var result = PlanValidator.Validate(
                $$"""
                {
                  "schemaVersion": 1,
                  "name": "T",
                  "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
                  "actions": [
                    { "type": "open_path", "id": "a1", "path": "C:\\Test\\{{spelling}}" }
                  ]
                }
                """,
                new PolicyOptions { AllowedRoots = [@"C:\Test"] });

            Assert.False(result.IsValid, $"\"{spelling}\" was accepted");
        }
    }
}

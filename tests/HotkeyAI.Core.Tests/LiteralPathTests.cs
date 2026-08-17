using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// A literal path that can never work is refused before someone approves it.
/// </summary>
/// <remarks>
/// The policy layer used to check literal paths only on
/// <c>launch_process.path</c>. Out-of-root literals on <c>open_path</c>, <c>list_files</c>,
/// <c>list_directories</c>, <c>path_exists</c>, <c>workingDirectory</c> and
/// <c>expect.path_exists</c> all validated clean and failed only at run time.
/// <para>
/// The runtime guard held throughout, so this is honesty rather than a hole — and the honesty
/// matters most on the approval screen, where someone is being asked whether to trust a plan.
/// "This cannot run" is something to learn there, not on the keypress.
/// </para>
/// </remarks>
public sealed class LiteralPathTests
{
    private static readonly PolicyOptions Rooted = new() { AllowedRoots = [@"C:\Test"] };

    private static ValidationResult Check(string actions, PolicyOptions? options = null) =>
        PlanValidator.Validate(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [{ "name": "found", "type": "boolean" },
                            { "name": "items", "type": "pathList" },
                            { "name": "item", "type": "path" },
                            { "name": "somewhere", "type": "path" }],
              "actions": [{{actions}}]
            }
            """,
            options ?? Rooted);

    private static string Message(ValidationResult result) =>
        string.Join(" | ", result.Errors.Select(e => e.ToString()));

    [Theory]
    [InlineData("""{ "type": "open_path", "id": "a1", "path": "C:\\Elsewhere\\thing.txt" }""")]
    [InlineData("""{ "type": "list_files", "id": "a1", "path": "C:\\Elsewhere", "into": "items" }""")]
    [InlineData("""{ "type": "list_directories", "id": "a1", "path": "C:\\Elsewhere", "into": "items" }""")]
    [InlineData("""{ "type": "path_exists", "id": "a1", "path": "C:\\Elsewhere", "into": "found" }""")]
    public void AnOutOfRootLiteralIsRefusedOnEveryActionThatTakesAPath(string action)
    {
        var result = Check(action);

        Assert.False(result.IsValid);
        Assert.Contains("not under an allowed root", Message(result), StringComparison.Ordinal);

        // And it says why it matters, which is the whole point of the finding.
        Assert.Contains("could be approved and still never work", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRootWorkingDirectoryIsRefused()
    {
        var result = Check(
            """
            { "type": "launch_process", "id": "a1", "app": "notepad",
              "workingDirectory": "C:\\Elsewhere" }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/actions/0/workingDirectory");
    }

    [Fact]
    public void AnOutOfRootPathInsideAnExpectationIsRefused()
    {
        // Postconditions are reached by the same reflective walk, which is why they are covered
        // without being named.
        var result = Check(
            """
            { "type": "set_clipboard", "id": "a1", "text": "x",
              "expect": { "type": "path_exists", "path": "C:\\Elsewhere\\thing.txt" } }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/actions/0/expect/path");
    }

    [Fact]
    public void AnOutOfRootPathInsideANestedPredicateIsRefused()
    {
        var result = Check(
            """
            { "type": "if", "id": "a1",
              "condition": { "type": "all_of", "conditions": [
                  { "type": "process_running", "processName": "explorer" },
                  { "type": "path_exists", "path": "C:\\Elsewhere" } ] },
              "then": [ { "type": "wait", "durationMs": 10 } ] }
            """);

        Assert.False(result.IsValid);
        Assert.Contains("not under an allowed root", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativePathIsRefusedEvenWithNoRootsConfigured()
    {
        // Different question from the root check, and one that has an answer regardless: an
        // automation runs from wherever the agent happens to be, so a relative path has nothing to
        // be relative to.
        var result = Check(
            """{ "type": "open_path", "id": "a1", "path": "notes\\todo.txt" }""",
            PolicyOptions.Default);

        Assert.False(result.IsValid);
        Assert.Contains("not an absolute Windows path", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoRootsConfiguredAnAbsolutePathIsLeftAlone()
    {
        // With no roots the run-time guard refuses everything anyway, and a validator that rejected
        // every literal under PolicyOptions.Default would be useless for authoring.
        var result = Check(
            """{ "type": "open_path", "id": "a1", "path": "C:\\Anywhere\\notes.txt" }""",
            PolicyOptions.Default);

        Assert.True(result.IsValid, Message(result));
    }

    [Fact]
    public void AnInRootLiteralIsFine()
    {
        var result = Check("""{ "type": "open_path", "id": "a1", "path": "C:\\Test\\notes.txt" }""");

        Assert.True(result.IsValid, Message(result));
    }

    [Fact]
    public void AnInterpolatedPathIsLeftToTheExecutor()
    {
        // Nothing can be checked statically, and the executor re-checks the resolved value against
        // the same roots. Refusing the shape would forbid every "open what I picked".
        var result = Check(
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "items" },
            { "type": "foreach", "id": "a2", "source": "items", "itemVariable": "item",
              "body": [ { "type": "open_path", "id": "a3", "path": "${item.fullPath}" } ] }
            """);

        Assert.True(result.IsValid, Message(result));
    }

    [Fact]
    public void LaunchProcessKeepsItsOwnStricterMessage()
    {
        // launch_process refuses an interpolated path outright rather than re-checking it, and the
        // new walk must not report the same field a second time in different words.
        var result = Check(
            """{ "type": "launch_process", "id": "a1", "path": "${somewhere}" }""");

        Assert.False(result.IsValid);

        // The stricter launch-specific message, and no second sentence about roots saying the same
        // thing differently. (The dataflow layer also reports the unassigned variable, which is a
        // genuinely different problem with the same pointer.)
        Assert.Contains(
            result.Errors,
            e => e.Path == "/actions/0/path"
                 && e.Message.Contains("built from a variable", StringComparison.Ordinal));

        Assert.DoesNotContain(
            result.Errors,
            e => e.Message.Contains("not under an allowed root", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOutOfRootLaunchPathIsStillReportedOnceOnly()
    {
        var result = Check("""{ "type": "launch_process", "id": "a1", "path": "C:\\Elsewhere\\a.exe" }""");

        Assert.False(result.IsValid);
        Assert.Single(result.Errors, e => e.Path == "/actions/0/path");
    }
}

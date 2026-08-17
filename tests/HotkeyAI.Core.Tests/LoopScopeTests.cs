using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// A <c>foreach</c> item variable exists only inside its loop.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding M3. The dataflow check tracked loop item variables in a
/// <c>HashSet</c>, so once a <c>foreach</c> declared one the name counted as assigned for the rest
/// of the plan — including after the loop had ended and the engine had cleared it. A read there
/// interpolated to the empty string, which is the same class of silent-empty problem as finding M1:
/// <c>path_exists</c> on nothing, <c>contains: ""</c> matching everything.
/// <para>
/// The set became a dictionary from item variable to the JSON pointer of the loop that owns it, and
/// "outside the loop" is decided by pointer prefix — which works because the walk is depth-first.
/// </para>
/// </remarks>
public sealed class LoopScopeTests
{
    private static readonly PolicyOptions Policy = new() { AllowedRoots = [@"C:\Test"] };

    private static ValidationResult Check(string variables, string actions) =>
        PlanValidator.Validate(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [{{variables}}],
              "actions": [{{actions}}]
            }
            """,
            Policy);

    private static string Message(ValidationResult result) =>
        string.Join(" | ", result.Errors.Select(e => e.ToString()));

    private const string Repos = """{ "name": "repos", "type": "pathList" }""";
    private const string Repo = """{ "name": "repo", "type": "path" }""";

    [Fact]
    public void TheItemVariableIsUsableInsideItsOwnLoop()
    {
        // The ordinary case, which must stay ordinary.
        var result = Check(
            $"{Repos}, {Repo}",
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "repo",
              "body": [ { "type": "notify", "id": "a3", "message": "${repo.name}" } ] }
            """);

        Assert.True(result.IsValid, Message(result));
    }

    [Fact]
    public void ReadingItAfterTheLoopIsRefused()
    {
        var result = Check(
            $"{Repos}, {Repo}",
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "repo",
              "body": [ { "type": "notify", "id": "a3", "message": "${repo.name}" } ] },
            { "type": "notify", "id": "a4", "message": "Last one was ${repo.name}" }
            """);

        Assert.False(result.IsValid);

        var message = Message(result);
        Assert.Contains("only exists inside its loop", message, StringComparison.Ordinal);

        // And it says what would happen, not just that it is wrong.
        Assert.Contains("interpolate an empty string", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerNamesTheReadAfterTheLoop()
    {
        var result = Check(
            $"{Repos}, {Repo}",
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "repo",
              "body": [ { "type": "wait", "durationMs": 10 } ] },
            { "type": "notify", "id": "a3", "message": "${repo}" }
            """);

        Assert.Contains(result.Errors, e => e.Path == "/actions/2/message");
    }

    [Fact]
    public void ANestedLoopStillReachesTheOuterItem()
    {
        // The inner loop's actions are inside the outer loop's pointer, so the prefix test has to
        // let the outer item through — a plain "is this action the loop's direct child" rule would
        // not.
        var result = Check(
            $$"""
            {{Repos}}, {{Repo}},
            { "name": "files", "type": "pathList" },
            { "name": "file", "type": "path" }
            """,
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "repo",
              "body": [
                { "type": "list_files", "id": "a3", "path": "C:\\Test", "into": "files" },
                { "type": "foreach", "id": "a4", "source": "files", "itemVariable": "file",
                  "body": [ { "type": "notify", "id": "a5",
                            "message": "${repo.name}/${file.name}" } ] } ] }
            """);

        Assert.True(result.IsValid, Message(result));
    }

    [Fact]
    public void AnInnerItemDoesNotEscapeIntoTheOuterLoop()
    {
        // Same loop nesting, but the read of the inner item happens after the inner loop has ended
        // while still inside the outer one — so "outside the loop that owns it" has to be per-loop,
        // not per-plan.
        var result = Check(
            $$"""
            {{Repos}}, {{Repo}},
            { "name": "files", "type": "pathList" },
            { "name": "file", "type": "path" }
            """,
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "repo",
              "body": [
                { "type": "foreach", "id": "a3", "source": "files", "itemVariable": "file",
                  "body": [ { "type": "wait", "durationMs": 10 } ] },
                { "type": "notify", "id": "a4", "message": "${file.name}" } ] }
            """);

        Assert.False(result.IsValid);
        Assert.Contains("only exists inside its loop", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void WritingTheNameAfterTheLoopClearsItsLoopOwnership()
    {
        // Reusing a name is legal, and after a real write it is an ordinary variable again. Without
        // clearing ownership the refusal would follow the name around for the rest of the plan and
        // reject a plan that is fine.
        var result = Check(
            """
            { "name": "repos", "type": "pathList" },
            { "name": "chosen", "type": "path" }
            """,
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "chosen",
              "body": [ { "type": "wait", "durationMs": 10 } ] },
            { "type": "show_picker", "id": "a3", "source": "repos", "into": "chosen",
              "prompt": "Pick one" },
            { "type": "notify", "id": "a4", "message": "${chosen.name}" }
            """);

        Assert.True(result.IsValid, Message(result));
    }

    [Fact]
    public void TheExpectationOfAnActionAfterTheLoopIsCheckedToo()
    {
        // Expectations are walked separately from the action's own properties, so the loop-scope
        // check has to be applied on both sides of the write — it is easy to add it to one only.
        var result = Check(
            $"{Repos}, {Repo}",
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "repo",
              "body": [ { "type": "wait", "durationMs": 10 } ] },
            { "type": "notify", "id": "a3", "message": "done",
              "expect": { "type": "path_exists", "path": "${repo}" } }
            """);

        Assert.False(result.IsValid);
        Assert.Contains("only exists inside its loop", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoopsOwnSourceIsNotTreatedAsAnEscapedRead()
    {
        // The foreach action's own pointer equals the owner pointer, and it reads source rather than
        // the item — so the equality case in Escaped has to be excluded or every loop over a list
        // whose item shares a name with something else would be refused.
        var result = Check(
            """
            { "name": "repos", "type": "pathList" },
            { "name": "repos_item", "type": "path" }
            """,
            """
            { "type": "list_directories", "id": "a1", "path": "C:\\Test", "into": "repos" },
            { "type": "foreach", "id": "a2", "source": "repos", "itemVariable": "repos_item",
              "body": [ { "type": "notify", "id": "a3", "message": "${repos_item}" } ] }
            """);

        Assert.True(result.IsValid, Message(result));
    }
}

using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// A postcondition must never report success while checking nothing.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding M1. An unset variable interpolates to the empty string, so
/// <c>contains: "${ghost}"</c> became <c>Contains("")</c> — true of every string — and the step was
/// logged as <c>(verified)</c> with the clipboard holding something entirely unrelated.
/// <para>
/// This is the one failure the engine's honesty story cannot absorb. The whole purpose of counting
/// unverified actions is that "it ran" and "it worked" stay separate claims, and a vacuous check
/// silently promotes the weaker one. The review's probe asserted the buggy behaviour; these are the
/// same observations with the assertions inverted.
/// </para>
/// </remarks>
public sealed class VacuousVerificationTests
{
    private static readonly string[] Roots = [@"C:\Users\test\Projects"];

    private static Automation Plan(string actions, string variables = "") =>
        JsonSerializer.Deserialize<Automation>(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [{{variables}}],
              "actions": [{{actions}}]
            }
            """,
            DslJson.Options)!;

    private static PlanExecutor Executor(FakeDesktop desktop) =>
        new(desktop, new PathGuard(Roots));

    [Fact]
    public async Task AnEmptyContainsFailsRatherThanPassing()
    {
        var desktop = new FakeDesktop { ClipboardText = "totally unrelated payload" };

        // ${ghost} is declared but never written, which is what the validator used to let through.
        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "set_clipboard", "id": "a1", "text": "hello",
                   "expect": { "type": "clipboard_matches", "contains": "${ghost}" } }
                 """,
                 """{ "name": "ghost", "type": "text" }"""),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        var entry = Assert.Single(result.Entries, e => e.ActionType == "set_clipboard");
        Assert.Equal(Verification.Failed, entry.Verification);

        // And it says why, rather than reporting a generic miss after waiting out the window.
        Assert.Contains("interpolated to nothing", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyPathExpectationFailsForTheStatedReason()
    {
        // Previously caught by the path guard, but by accident rather than intent — so the message
        // talked about allowed roots when the real problem was an unwritten variable.
        var result = await Executor(new FakeDesktop()).RunAsync(
            Plan("""
                 { "type": "set_clipboard", "id": "a1", "text": "x",
                   "expect": { "type": "path_exists", "path": "${ghost}" } }
                 """,
                 """{ "name": "ghost", "type": "path" }"""),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Entries,
            e => e.Detail.Contains("interpolated to nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARealContainsStillPasses()
    {
        // The fix must not have turned every clipboard check into a failure.
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "set_clipboard", "id": "a1", "text": "the quick brown fox",
                   "expect": { "type": "clipboard_matches", "contains": "quick" } }
                 """),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Entries, e => e.Verification == Verification.Passed);
    }

    [Fact]
    public async Task AnEmptyEqualsIsStillARealComparison()
    {
        // "equals" against an empty string is a legitimate question — is the clipboard empty — so
        // it must keep working rather than being swept up with the vacuous case. Note the wire
        // name is "equals", not "exactly": the C# property is renamed only because a record
        // already generates an Equals member.
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "set_clipboard", "id": "a1", "text": "",
                   "expect": { "type": "clipboard_matches", "equals": "" } }
                 """),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
    }

    [Fact]
    public async Task AnExpectationWithNeitherFieldFailsRatherThanPassing()
    {
        // Found while writing these: the schema cannot express "one of contains or equals is
        // required" — that needs oneOf, which is outside the structured-output subset — so a
        // clipboard_matches carrying neither used to pass vacuously. The same fix covers it,
        // which is worth pinning because nothing else does.
        var result = await Executor(new FakeDesktop()).RunAsync(
            Plan("""
                 { "type": "set_clipboard", "id": "a1", "text": "anything",
                   "expect": { "type": "clipboard_matches" } }
                 """),
            CancellationToken.None);

        Assert.False(result.Succeeded);
    }
}

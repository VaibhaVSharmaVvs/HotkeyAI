using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// Nothing that came from outside the plan reaches a log line.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding M8. <c>get_clipboard</c> and <c>type_text</c> were carefully
/// kept out of the log, and then <c>abort.reason</c> interpolated the same clipboard text into a step
/// detail — which becomes the transcript, the file under <c>%LOCALAPPDATA%\HotkeyAI\logs</c>, and the
/// repair prompt PLAN.md expects people to paste somewhere. The reviewer's own transcript showed an
/// AWS key and a password arriving there:
/// <code>
/// [a2] abort: Aborted - bailing out with AKIAIOSFODNN7EXAMPLE / hunter2
/// </code>
/// <para>
/// Provenance is the fix rather than pattern-matching the value, because a secret does not look like
/// anything in particular. What is known for certain is where it came from.
/// </para>
/// </remarks>
public sealed class LogRedactionTests
{
    private const string Secret = "AKIAIOSFODNN7EXAMPLE / hunter2";

    private static readonly string[] Roots = [@"C:\Users\test\Projects"];

    private static Automation Plan(string actions, string variables) =>
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
    public async Task ClipboardTextDoesNotReachTheAbortReason()
    {
        var desktop = new FakeDesktop { ClipboardText = Secret };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "clip" },
                 { "type": "abort", "id": "a2", "reason": "bailing out with ${clip}" }
                 """,
                 """{ "name": "clip", "type": "text" }"""),
            CancellationToken.None);

        var transcript = result.ToTranscript();

        Assert.DoesNotContain(Secret, transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRedactionNamesTheVariableSoTheTranscriptStillExplainsItself()
    {
        // A blank would leave "bailing out with " and nobody able to tell why. The shape of what
        // happened is not the secret.
        var desktop = new FakeDesktop { ClipboardText = Secret };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "clip" },
                 { "type": "abort", "id": "a2", "reason": "bailing out with ${clip}" }
                 """,
                 """{ "name": "clip", "type": "text" }"""),
            CancellationToken.None);

        Assert.Contains("bailing out with [clip redacted]", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APromptAnswerIsTreatedTheSameWay()
    {
        // show_input is the other source of text nobody chose to write down, and it is the more
        // likely of the two to be an actual password.
        var desktop = new FakeDesktop { InputAnswer = "correct horse battery staple" };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "show_input", "id": "a1", "prompt": "Token?", "into": "answer" },
                 { "type": "abort", "id": "a2", "reason": "got ${answer}" }
                 """,
                 """{ "name": "answer", "type": "text" }"""),
            CancellationToken.None);

        Assert.DoesNotContain("correct horse", result.ToTranscript(), StringComparison.Ordinal);
        Assert.Contains("[answer redacted]", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingForAPropertyOfItIsRedactedToo()
    {
        // ${clip.name} of clipboard text is still clipboard text — the check has to happen before
        // the property is applied, not after.
        var desktop = new FakeDesktop { ClipboardText = @"C:\secrets\hunter2.txt" };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "clip" },
                 { "type": "abort", "id": "a2", "reason": "was ${clip.name}" }
                 """,
                 """{ "name": "clip", "type": "text" }"""),
            CancellationToken.None);

        Assert.DoesNotContain("hunter2", result.ToTranscript(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVariableThePlanWroteItselfIsNotRedacted()
    {
        // The log has to stay useful. A value the plan put there is a value the plan author already
        // knows, and redacting it would buy nothing.
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "path_exists", "id": "a1", "path": "C:\\Users\\test\\Projects",
                   "into": "found" },
                 { "type": "abort", "id": "a2", "reason": "found was ${found}" }
                 """,
                 """{ "name": "found", "type": "boolean" }"""),
            CancellationToken.None);

        // False, because FakeDesktop has no such directory — the value is beside the point, the
        // point is that it is rendered rather than redacted.
        Assert.Contains("found was false", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReassigningTheNameFromInsideThePlanClearsTheMarking()
    {
        // Reusing a name is legal. A variable that held clipboard text and was then written by
        // path_exists holds a boolean the plan computed, and redacting that forever would make the
        // log worse for no gain.
        var desktop = new FakeDesktop { ClipboardText = Secret };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "reused" },
                 { "type": "path_exists", "id": "a2", "path": "C:\\Users\\test\\Projects",
                   "into": "reused" },
                 { "type": "abort", "id": "a3", "reason": "now ${reused}" }
                 """,
                 """{ "name": "reused", "type": "text" }, { "name": "spare", "type": "boolean" }"""),
            CancellationToken.None);

        // Whatever the type juggling reports, the point is that it is not redacted and not the
        // secret.
        Assert.DoesNotContain("redacted", result.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClipboardStillReachesTheDesktopUnredacted()
    {
        // Only what gets written down is redacted. An automation whose whole job is to paste the
        // clipboard somewhere must keep working, so set_clipboard and type_text still interpolate
        // the real value.
        var desktop = new FakeDesktop { ClipboardText = Secret };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "clip" },
                 { "type": "type_text", "id": "a2", "text": "${clip}" }
                 """,
                 """{ "name": "clip", "type": "text" }"""),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
        Assert.Contains(desktop.Effects, e => e == $"type:{Secret}");

        // And still not in the log.
        Assert.DoesNotContain(Secret, result.ToTranscript(), StringComparison.Ordinal);
    }
}

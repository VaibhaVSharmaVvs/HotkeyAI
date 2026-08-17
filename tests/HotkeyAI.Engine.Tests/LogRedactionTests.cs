using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// Nothing that came from outside the plan reaches a log line.
/// </summary>
/// <remarks>
/// <c>get_clipboard</c> and <c>type_text</c> were carefully kept out of the log, and then
/// <c>abort.reason</c> interpolated the same clipboard text into a step detail — which becomes the
/// transcript, the file under <c>%LOCALAPPDATA%\HotkeyAI\logs</c>, and the repair prompt PLAN.md
/// expects people to paste somewhere. An AWS key and a password reached it that way:
/// <code>
/// [a2] abort: Aborted - bailing out with AKIAIOSFODNN7EXAMPLE / hunter2
/// </code>
/// <para>
/// Provenance is the fix rather than pattern-matching the value, because a secret does not look
/// like anything in particular. What is known for certain is where it came from.
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

    // ------------------------------- the other five sites -------------------------------

    /// <summary>
    /// Every handler that interpolates a path and then logs it, refusal path included.
    /// </summary>
    /// <remarks>
    /// Switching <c>abort.reason</c> to the redacting interpolation was not the end of it. The
    /// refusal path is the general leak: the path guard refuses <em>any</em> value that is not a
    /// valid in-root path, and quoted the value it was given — so clipboard text that is not a path
    /// at all was echoed verbatim into the transcript, the agent log and the repair prompt.
    /// <para>
    /// Five handlers, not the three that first look guilty: <c>launch_process</c>'s executable and
    /// its <c>workingDirectory</c> leaked the same way.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("path_exists", """{ "type": "path_exists", "id": "a2", "path": "${clip}", "into": "found" }""")]
    [InlineData("open_path", """{ "type": "open_path", "id": "a2", "path": "${clip}" }""")]
    [InlineData("list_files", """{ "type": "list_files", "id": "a2", "path": "${clip}", "into": "items" }""")]
    [InlineData("list_directories", """{ "type": "list_directories", "id": "a2", "path": "${clip}", "into": "items" }""")]
    [InlineData("workingDirectory", """{ "type": "launch_process", "id": "a2", "app": "notepad", "workingDirectory": "${clip}" }""")]
    public async Task ARefusalDoesNotEchoTheClipboard(string label, string action)
    {
        var desktop = new FakeDesktop { ClipboardText = Secret };

        // The workingDirectory case launches an app, and an unresolvable app fails before the
        // directory is ever checked — which would pass this test for the wrong reason.
        desktop.InstalledApps["notepad"] = @"C:\Windows\notepad.exe";

        var result = await Executor(desktop).RunAsync(
            Plan(
                $$"""
                  { "type": "get_clipboard", "id": "a1", "into": "clip" },
                  {{action}}
                  """,
                """
                { "name": "clip", "type": "text" },
                { "name": "found", "type": "boolean" },
                { "name": "items", "type": "pathList" }
                """),
            CancellationToken.None);

        var transcript = result.ToTranscript();

        Assert.False(result.Succeeded, label);
        Assert.DoesNotContain(Secret, transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.FailureReason!, StringComparison.Ordinal);

        // Still says which variable, so the transcript stays diagnosable.
        Assert.Contains("[clip redacted]", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessLineDoesNotEchoTheClipboardEither()
    {
        // The engine cannot tell a path-shaped secret from a path, so the success detail is
        // redacted on the same rule as the refusal. Less specific than before, and correct.
        var desktop = new FakeDesktop { ClipboardText = @"C:\Users\test\Projects\hunter2.txt" };
        desktop.ExistingPaths.Add(@"C:\Users\test\Projects\hunter2.txt");

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "clip" },
                 { "type": "path_exists", "id": "a2", "path": "${clip}", "into": "found" }
                 """,
                 """
                 { "name": "clip", "type": "text" },
                 { "name": "found", "type": "boolean" }
                 """),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
        Assert.DoesNotContain("hunter2", result.ToTranscript(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGuardStillChecksTheRealValueNotTheRedactedOne()
    {
        // The failure this fix could easily introduce. If the redacted string were handed to the
        // guard, "[clip redacted]" would be checked instead of the path — every clipboard path
        // would be refused, and worse, the boundary would be deciding about a string the OS never
        // sees.
        var desktop = new FakeDesktop { ClipboardText = @"C:\Users\test\Projects\notes.txt" };
        desktop.ExistingPaths.Add(@"C:\Users\test\Projects\notes.txt");

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "clip" },
                 { "type": "open_path", "id": "a2", "path": "${clip}" }
                 """,
                 """{ "name": "clip", "type": "text" }"""),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());

        // The desktop was handed the real path, redaction or no redaction.
        Assert.Contains(desktop.Effects, e => e == @"open:C:\Users\test\Projects\notes.txt");
    }

    [Fact]
    public async Task APathThePlanWroteItselfIsStillNamedInFull()
    {
        // show_picker uses the ordinary setter, so "open what I picked" still logs the real path —
        // which is most of what these logs are read for.
        var desktop = new FakeDesktop { PickerChoice = @"C:\Users\test\Projects\chosen" };
        desktop.Directories[@"C:\Users\test\Projects"] = [@"C:\Users\test\Projects\chosen"];

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "list_directories", "id": "a1", "path": "C:\\Users\\test\\Projects",
                   "into": "items" },
                 { "type": "show_picker", "id": "a2", "source": "items", "prompt": "Which?",
                   "into": "picked" },
                 { "type": "open_path", "id": "a3", "path": "${picked}" }
                 """,
                 """
                 { "name": "items", "type": "pathList" },
                 { "name": "picked", "type": "path" }
                 """),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
        Assert.Contains("chosen", result.ToTranscript(), StringComparison.Ordinal);
        Assert.DoesNotContain("redacted", result.ToTranscript(), StringComparison.Ordinal);
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

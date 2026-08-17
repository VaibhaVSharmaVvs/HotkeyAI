using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The transcript says what actually stopped the run, and always says something.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, findings L6 and L10. The transcript is not decoration: PLAN.md
/// expects people to paste it into a repair prompt, so a sentence that is confidently wrong sends
/// both the reader and a planner after the wrong thing, and a missing sentence leaves them with
/// nothing at all.
/// <para>
/// L6 — the cancellation reason was hardcoded to "Stopped by the panic key." for any cancellation,
/// so the dashboard's Stop button produced a transcript blaming a key nobody pressed. L10 — the
/// generic-exception path stopped the run without writing a log entry, so the transcript simply
/// ended.
/// </para>
/// </remarks>
public sealed class TranscriptHonestyTests
{
    private static readonly string[] Roots = [@"C:\Users\test\Projects"];

    private static Automation Plan(string actions) =>
        JsonSerializer.Deserialize<Automation>(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [{{actions}}]
            }
            """,
            DslJson.Options)!;

    private static PlanExecutor Executor(FakeDesktop desktop) =>
        new(desktop, new PathGuard(Roots));

    private const string ThreeNotifies = """
        { "type": "notify", "id": "a1", "message": "one" },
        { "type": "notify", "id": "a2", "message": "two" },
        { "type": "notify", "id": "a3", "message": "three" }
        """;

    private static FakeDesktop CancellingOnFirstEffect(CancellationTokenSource source)
    {
        var desktop = new FakeDesktop();
        desktop.OnEffect = _ => { source.Cancel(); return Task.CompletedTask; };
        return desktop;
    }

    // ------------------------------- L6 -------------------------------

    [Fact]
    public async Task TheCallerDecidesWhatStoppedIt()
    {
        using var stop = new CancellationTokenSource();
        var desktop = CancellingOnFirstEffect(stop);

        var result = await Executor(desktop)
            .RunAsync(Plan(ThreeNotifies), null, () => "Stopped by the Stop button.", stop.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("Stop button", result.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("panic key", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSameEngineReportsThePanicKeyWhenThatIsWhatHappened()
    {
        using var panic = new CancellationTokenSource();
        var desktop = CancellingOnFirstEffect(panic);

        var result = await Executor(desktop)
            .RunAsync(Plan(ThreeNotifies), null, () => "Stopped by the panic key.", panic.Token);

        Assert.Contains("panic key", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerThatCannotSayGetsSomethingTrueRatherThanAGuess()
    {
        // Being vague beats naming a mechanism nobody invoked, which is what the old hardcoded text
        // did every time the Stop button was used.
        using var stop = new CancellationTokenSource();
        var desktop = CancellingOnFirstEffect(stop);

        var result = await Executor(desktop).RunAsync(Plan(ThreeNotifies), stop.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("Stopped before it finished.", result.FailureReason);
    }

    [Fact]
    public async Task ADescriberThatThrowsDoesNotBecomeTheFailure()
    {
        // It runs on the abort path, where an exception would replace a clean abort — and the modifier
        // release has already happened by then, so losing the rest would be a stuck Ctrl key.
        using var stop = new CancellationTokenSource();
        var desktop = CancellingOnFirstEffect(stop);

        var result = await Executor(desktop).RunAsync(
            Plan(ThreeNotifies),
            null,
            () => throw new InvalidOperationException("the window was already closed"),
            stop.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("Stopped before it finished.", result.FailureReason);
        Assert.Equal(1, desktop.ModifiersReleasedCount);
    }

    [Fact]
    public async Task AnEmptyDescriptionFallsBackRatherThanLeavingItBlank()
    {
        using var stop = new CancellationTokenSource();
        var desktop = CancellingOnFirstEffect(stop);

        var result = await Executor(desktop)
            .RunAsync(Plan(ThreeNotifies), null, () => "", stop.Token);

        Assert.Equal("Stopped before it finished.", result.FailureReason);
    }

    // ------------------------------- L10 -------------------------------

    /// <summary>
    /// A plan whose postcondition asks about a process, so a throwing desktop escapes the
    /// per-action catch.
    /// </summary>
    /// <remarks>
    /// An exception from a dispatch is caught per action and reported as one failed step, by design —
    /// "one bad action must not abort the whole agent". So it never reaches the run-level handler this
    /// finding is about. Verification runs outside that guard, which is the shortest honest route to
    /// it, and is itself a realistic failure: the Win32 process APIs can throw.
    /// </remarks>
    private const string NotifyThenVerify = """
        { "type": "notify", "id": "a1", "message": "one",
          "expect": { "type": "process_running", "processName": "explorer" } }
        """;

    [Fact]
    public async Task AnUnexpectedEngineErrorWritesALineIntoTheTranscript()
    {
        // The cancellation path logged an entry; this one did not, so the transcript ended with no
        // explanation at all — the worst possible state for the one artefact someone takes away.
        var desktop = new FakeDesktop
        {
            ProcessCheckThrows = new InvalidOperationException("the desktop went away"),
        };

        var result = await Executor(desktop).RunAsync(
            Plan(NotifyThenVerify),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        var transcript = result.ToTranscript();
        Assert.Contains("the desktop went away", transcript, StringComparison.Ordinal);

        // And the type is named, because "unexpected" covers everything from a disposed window to a
        // Win32 failure and which one it was is the first thing worth knowing.
        Assert.Contains("InvalidOperationException", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedEngineErrorStillReleasesModifiers()
    {
        var desktop = new FakeDesktop
        {
            ProcessCheckThrows = new InvalidOperationException("boom"),
        };

        await Executor(desktop).RunAsync(Plan(NotifyThenVerify), CancellationToken.None);

        Assert.Equal(1, desktop.ModifiersReleasedCount);
    }

    [Fact]
    public async Task TheAbortLineIsTheLastThingInTheTranscript()
    {
        // Order matters for a log someone reads top to bottom: the explanation has to come after the
        // steps it explains, not before them.
        var desktop = new FakeDesktop
        {
            ProcessCheckThrows = new InvalidOperationException("boom"),
        };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "notify", "id": "a1", "message": "one" },
                 { "type": "notify", "id": "a2", "message": "two",
                   "expect": { "type": "process_running", "processName": "explorer" } }
                 """),
            CancellationToken.None);

        Assert.Equal("abort", result.Entries[^1].ActionType);

        // And the steps that did run are still above it.
        Assert.Contains(result.Entries, e => e.ActionId == "a1");
    }
}

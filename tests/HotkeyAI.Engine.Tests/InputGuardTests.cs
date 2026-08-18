using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The sensitive-window guard runs throughout a piece of input, not once before it.
/// </summary>
/// <remarks>
/// BlockedAsync used to be called once, with the whole operation then handed to the Windows layer,
/// where SendChordAsync looped up to <c>repeat</c> times (policy maximum 50) and TypeTextAsync sent
/// one character every 5 ms — so a 2 000-character payload occupied the foreground for ten seconds
/// after a single check. Anything taking focus in that window received the remainder: a UAC prompt
/// appearing, the user alt-tabbing to their password manager.
/// <para>
/// Both loops moved up into the executor, which is where the safety controls live. A loop inside
/// the Windows layer is a loop the controls cannot see into.
/// </para>
/// </remarks>
public sealed class InputGuardTests
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

    [Fact]
    public async Task TheGuardRunsOncePerRepeat()
    {
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan("""{ "type": "send_keys", "id": "a1", "keys": ["CTRL","W"], "repeat": 5 }"""),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
        Assert.Equal(5, desktop.Effects.Count(e => e.StartsWith("keys:", StringComparison.Ordinal)));
        Assert.Equal(5, desktop.HazardChecks);
    }

    [Fact]
    public async Task AHazardAppearingMidRepeatStopsTheRest()
    {
        var desktop = new FakeDesktop();

        // A consent prompt arrives after the second chord — the case the single up-front check
        // could not see, because by then it had already returned.
        desktop.OnEffect = effect =>
        {
            if (effect.StartsWith("keys:", StringComparison.Ordinal)
                && desktop.Effects.Count(e => e.StartsWith("keys:", StringComparison.Ordinal)) == 2)
            {
                desktop.Hazard = InputHazard.ConsentPrompt;
            }

            return Task.CompletedTask;
        };

        var result = await Executor(desktop).RunAsync(
            Plan("""{ "type": "send_keys", "id": "a1", "keys": ["CTRL","W"], "repeat": 20 }"""),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(2, desktop.Effects.Count(e => e.StartsWith("keys:", StringComparison.Ordinal)));

        // And it says how far it got, because two of those keystrokes have already landed.
        Assert.Contains("Stopped after 2 of 20 repeats", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongTextIsCheckedWhileItIsBeingTyped()
    {
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan($$"""
                  { "type": "type_text", "id": "a1", "text": "{{new string('a', 200)}}" }
                  """),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());

        // 200 characters in 32-character chunks: seven chunks, and a check before each.
        Assert.Equal(7, desktop.Effects.Count(e => e.StartsWith("type:", StringComparison.Ordinal)));
        Assert.Equal(7, desktop.HazardChecks);
    }

    [Fact]
    public async Task AHazardAppearingMidTypingStopsTheRestAndSaysHowMuchLanded()
    {
        var desktop = new FakeDesktop();

        desktop.OnEffect = effect =>
        {
            if (effect.StartsWith("type:", StringComparison.Ordinal))
            {
                desktop.Hazard = InputHazard.CredentialPrompt;
            }

            return Task.CompletedTask;
        };

        var result = await Executor(desktop).RunAsync(
            Plan($$"""
                  { "type": "type_text", "id": "a1", "text": "{{new string('b', 100)}}" }
                  """),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(desktop.Effects, e => e.StartsWith("type:", StringComparison.Ordinal));

        // The count is the point: those characters are somewhere, and the user has to go and look.
        Assert.Contains(
            "Stopped after 32 of 100 characters, which have already been typed",
            result.FailureReason!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FocusMovingMidTypingStopsTheRest()
    {
        // The hazards recognise a *dangerous* new window. This is the other case: a perfectly
        // ordinary window that came to the front, which Windows will happily deliver the rest of
        // the payload to. Nothing caught it before.
        var desktop = new FakeDesktop { ForegroundWindowId = 1 };

        desktop.OnEffect = effect =>
        {
            if (effect.StartsWith("type:", StringComparison.Ordinal))
            {
                desktop.ForegroundWindowId = 2;
            }

            return Task.CompletedTask;
        };

        var result = await Executor(desktop).RunAsync(
            Plan($$"""
                  { "type": "type_text", "id": "a1", "text": "{{new string('c', 100)}}" }
                  """),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            "A different window came to the front",
            result.FailureReason!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FocusMovingIsNotHeldAgainstAChord()
    {
        // Deliberate asymmetry. Changing the foreground window can be the entire point of a chord —
        // Alt+Tab, Win+D, Ctrl+W on the last tab — so refusing when it moves would break plans that
        // work exactly as written. Typing never intends to, which is why it gets the stricter rule.
        var desktop = new FakeDesktop { ForegroundWindowId = 1 };

        desktop.OnEffect = effect =>
        {
            if (effect.StartsWith("keys:", StringComparison.Ordinal))
            {
                desktop.ForegroundWindowId++;
            }

            return Task.CompletedTask;
        };

        var result = await Executor(desktop).RunAsync(
            Plan("""{ "type": "send_keys", "id": "a1", "keys": ["ALT","TAB"], "repeat": 4 }"""),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
        Assert.Equal(4, desktop.Effects.Count(e => e.StartsWith("keys:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ShortTextIsStillOneChunkAndOneCheck()
    {
        // The chunking must not have turned an ordinary short payload into several calls, which
        // would show up as visible stuttering in the target application.
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan("""{ "type": "type_text", "id": "a1", "text": "git status" }"""),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
        Assert.Equal("type:git status", Assert.Single(desktop.Effects));
        Assert.Equal(1, desktop.HazardChecks);
    }

    [Fact]
    public async Task EmptyTextIsStillGuardedAndTypesNothing()
    {
        var desktop = new FakeDesktop { Hazard = InputHazard.ElevatedWindow };

        var result = await Executor(desktop).RunAsync(
            Plan("""{ "type": "type_text", "id": "a1", "text": "" }"""),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, desktop.HazardChecks);
        Assert.DoesNotContain(desktop.Effects, e => e.StartsWith("type:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWholePayloadArrivesInOrder()
    {
        // Chunking is invisible to the target only if the pieces are the right pieces in the right
        // order — an off-by-one in the slicing would drop or duplicate characters, and nothing else
        // here would notice.
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => (char)('a' + (i % 26))));

        var desktop = new FakeDesktop();

        await Executor(desktop).RunAsync(
            Plan($$"""{ "type": "type_text", "id": "a1", "text": "{{text}}" }"""),
            CancellationToken.None);

        var typed = string.Concat(
            desktop.Effects
                .Where(e => e.StartsWith("type:", StringComparison.Ordinal))
                .Select(e => e["type:".Length..]));

        Assert.Equal(text, typed);
    }
}

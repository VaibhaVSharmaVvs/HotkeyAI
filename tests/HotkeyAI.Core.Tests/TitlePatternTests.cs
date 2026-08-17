using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// A window-title pattern the engine cannot run safely is refused before it can be installed.
/// </summary>
/// <remarks>
/// <c>WindowsWindows.Matches</c> used to guard its regex with a 250 ms timeout and a <c>catch</c>
/// filter naming a private marker class that nothing in the repository ever throws — so the real
/// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/> escaped, aborted the
/// window enumeration partway, and surfaced as a raw exception message.
/// <para>
/// The engine now matches titles on .NET's non-backtracking engine, which is linear in the input,
/// so no pattern can hang the sweep — a stronger answer than a backtracking heuristic in the policy
/// layer, because it is a guarantee rather than a guess. That engine refuses lookaround,
/// backreferences and atomic groups, and refuses them when the pattern is constructed. Discovering
/// that on a keypress would mean an automation the user approved failing at the moment they needed
/// it, so these tests pin that the policy layer says so first, with a pointer.
/// </para>
/// </remarks>
public sealed class TitlePatternTests
{
    private static ValidationResult Check(string selector) =>
        PlanValidator.Validate(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "focus_window", "id": "a1", "selector": {{selector}} }
              ]
            }
            """,
            PolicyOptions.Default);

    private static string Message(ValidationResult result) =>
        string.Join(" | ", result.Errors.Select(e => e.ToString()));

    [Fact]
    public void AnOrdinaryPatternIsFine()
    {
        Assert.True(Check("""{ "titleRegex": "^Build .* succeeded$" }""").IsValid);
    }

    [Fact]
    public void AnUnparseablePatternIsRefused()
    {
        // Previously this reached the desktop and threw mid-enumeration.
        var result = Check("""{ "titleRegex": "(unclosed" }""");

        Assert.False(result.IsValid);
        Assert.Contains("not a valid regular expression", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReviewsCatastrophicPatternIsAcceptedBecauseItIsNoLongerDangerous()
    {
        // ^(a+)+$ times out the ordinary engine on forty characters. On the linear-time engine it
        // answers in single-digit milliseconds, so there is nothing to refuse — and refusing it
        // would be the heuristic this fix deliberately avoids.
        Assert.True(Check("""{ "titleRegex": "^(a+)+$" }""").IsValid);
    }

    [Fact]
    public void LookaheadIsRefusedWithAnActionableMessage()
    {
        var result = Check("""{ "titleRegex": "(?=Visual)Studio" }""");

        Assert.False(result.IsValid);

        var message = Message(result);
        Assert.Contains("lookaround", message, StringComparison.Ordinal);

        // It has to say what to do instead, or the author is stuck.
        Assert.Contains("titleContains", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABackreferenceIsRefused()
    {
        var result = Check("""{ "titleRegex": "(ab)\\1" }""");

        Assert.False(result.IsValid);
        Assert.Contains("backreferences", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlongPatternIsRefused()
    {
        var pattern = new string('a', PolicyOptions.Default.MaxTitleRegexLength + 1);
        var result = Check($$"""{ "titleRegex": "{{pattern}}" }""");

        Assert.False(result.IsValid);
        Assert.Contains("over the limit of 200", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerNamesTheSelectorItself()
    {
        var result = Check("""{ "titleRegex": "(?=x)y" }""");

        Assert.Contains(
            result.Errors,
            e => e.Path == "/actions/0/selector/titleRegex");
    }

    [Fact]
    public void APatternInsideAnExpectationIsCheckedToo()
    {
        // The reflective walk exists for this: a selector can sit on the action, inside its expect,
        // or inside a predicate, and naming those places one by one is how the next one gets
        // missed.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "wait_for_window", "id": "a1",
                  "selector": { "titleContains": "x" },
                  "expect": { "type": "window_exists",
                              "selector": { "titleRegex": "(?=bad)" } } }
              ]
            }
            """,
            PolicyOptions.Default);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Path == "/actions/0/expect/selector/titleRegex");
    }

    [Fact]
    public void APatternInsideANestedPredicateIsCheckedToo()
    {
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "if", "id": "a1",
                  "condition": { "type": "all_of", "conditions": [
                      { "type": "process_running", "processName": "explorer" },
                      { "type": "window_exists",
                        "selector": { "titleRegex": "(ab)\\1" } } ] },
                  "then": [ { "type": "wait", "durationMs": 10 } ] }
              ]
            }
            """,
            PolicyOptions.Default);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Path.Contains("titleRegex", StringComparison.Ordinal));
    }

    [Fact]
    public void ASelectorInANestedActionIsReportedOnceUnderItsOwnPointer()
    {
        // The reflective walk must not descend into nested action lists — the outer walk already
        // visits those with their real pointers, so descending would report the same fault twice
        // under a pointer that does not exist in the document.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "if", "id": "a1",
                  "condition": { "type": "process_running", "processName": "explorer" },
                  "then": [
                    { "type": "focus_window", "id": "a2",
                      "selector": { "titleRegex": "(?=x)y" } } ] }
              ]
            }
            """,
            PolicyOptions.Default);

        var about = result.Errors
            .Where(e => e.Path.Contains("titleRegex", StringComparison.Ordinal))
            .ToList();

        var only = Assert.Single(about);
        Assert.Equal("/actions/0/then/0/selector/titleRegex", only.Path);
    }
}

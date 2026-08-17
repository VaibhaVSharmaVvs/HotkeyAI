using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// A variable read inside an expectation or a predicate counts as a read.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding M2. The dataflow check reflected over an action's own
/// properties but only understood strings, string collections and window selectors — so nested
/// postconditions and conditions fell through to nothing, and <c>${ghost}</c> inside them was
/// invisible. A plan reading a variable nothing ever wrote validated clean.
/// <para>
/// That is what made finding M1 reachable from a valid plan: the executor interpolated the unwritten
/// variable to the empty string and <c>contains: ""</c> reported "(verified)" while checking
/// nothing.
/// </para>
/// </remarks>
public sealed class NestedDataflowTests
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

    [Fact]
    public void AnUndeclaredVariableInsideAnExpectationIsCaught()
    {
        var result = Check("", """
            { "type": "get_clipboard", "id": "a1", "into": "got",
              "expect": { "type": "clipboard_matches", "contains": "${ghost}" } }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ToString().Contains("ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndeclaredVariableInsideAPredicateIsCaught()
    {
        var result = Check("", """
            { "type": "if", "id": "a1",
              "condition": { "type": "path_exists", "path": "${ghost}" },
              "then": [ { "type": "wait", "durationMs": 10 } ] }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ToString().Contains("ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndeclaredVariableInsideACompositeConditionIsCaught()
    {
        // all_of holds a list of conditions, so the walk has to descend through the collection as
        // well as through the records in it.
        var result = Check("", """
            { "type": "if", "id": "a1",
              "condition": { "type": "all_of", "conditions": [
                  { "type": "process_running", "processName": "explorer" },
                  { "type": "path_exists", "path": "${ghost}" } ] },
              "then": [ { "type": "wait", "durationMs": 10 } ] }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ToString().Contains("ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndeclaredVariableInsideANestedSelectorIsCaught()
    {
        var result = Check("", """
            { "type": "wait_for_window", "id": "a1",
              "selector": { "titleContains": "x" },
              "expect": { "type": "window_exists",
                          "selector": { "titleContains": "${ghost}" } } }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ToString().Contains("ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclaredAndWrittenVariableInsideAnExpectationIsFine()
    {
        // The check must not have become a blanket refusal of variables in expectations, which is
        // a perfectly ordinary thing for a plan to do.
        var result = Check(
            """{ "name": "got", "type": "text" }""",
            """
            { "type": "get_clipboard", "id": "a1", "into": "got",
              "expect": { "type": "clipboard_matches", "contains": "${got}" } }
            """);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ToString())));
    }

    [Fact]
    public void ADeclaredButNeverWrittenVariableInsideAnExpectationIsStillCaught()
    {
        // Declared is not the same as assigned, and the review's own probe used the declared case.
        var result = Check(
            """{ "name": "ghost", "type": "text" }""",
            """
            { "type": "get_clipboard", "id": "a1", "into": "got",
              "expect": { "type": "clipboard_matches", "contains": "${ghost}" } }
            """);

        Assert.False(result.IsValid);
    }
}

using HotkeyAI.Core;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The validator must describe the user's mistake, not the evaluator's search.
/// </summary>
/// <remarks>
/// Three ways the validator used to say something
/// other than what was wrong. None is exploitable; all three cost the reader time, and two of them
/// are read by a planner in a fix loop, where a message describing the wrong thing produces the
/// wrong repair.
/// </remarks>
public sealed class ValidatorHonestyTests
{
    private static readonly PolicyOptions Policy = new() { AllowedRoots = [@"C:\Test"] };

    private static string Message(ValidationResult result) =>
        string.Join(" | ", result.Errors.Select(e => e.ToString()));

    // --------------------------- numbers out of range ---------------------------

    [Fact]
    public void ANumberTooLargeForInt32BlamesThePlanNotTheTool()
    {
        // Reported "This is a defect in Hotkey AI, not in the plan." — misattributed, because the
        // schema's "integer" carries no range and nothing else looked before deserialisation threw.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [ { "type": "wait", "id": "a1", "durationMs": 99999999999999999999 } ]
            }
            """,
            Policy);

        Assert.False(result.IsValid);

        var message = Message(result);
        Assert.DoesNotContain("defect in Hotkey AI", message, StringComparison.Ordinal);
        Assert.Contains("32-bit integer", message, StringComparison.Ordinal);

        // And the pointer reaches the number itself, rather than the document root.
        Assert.Contains(result.Errors, e => e.Path == "/actions/0/durationMs");
    }

    [Fact]
    public void ANegativeNumberBelowInt32IsCaughtToo()
    {
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [ { "type": "wait", "id": "a1", "durationMs": -99999999999999999999 } ]
            }
            """,
            Policy);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/actions/0/durationMs");
    }

    [Fact]
    public void AnOrdinaryNumberIsUntouched()
    {
        // The pre-pass runs over every document, so it must not have opinions about normal values —
        // the property's own bounds are the policy layer's business.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [ { "type": "wait", "id": "a1", "durationMs": 250 } ]
            }
            """,
            Policy);

        Assert.True(result.IsValid, Message(result));
    }

    // ------------------------------ branch noise ------------------------------

    [Fact]
    public void AnUnknownFieldIsOneErrorThatNamesTheField()
    {
        // Two errors before: the object's additionalProperties failure, and "All values fail
        // against the false schema" on the property — evaluator vocabulary that means nothing to
        // anyone.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "open_path", "id": "a1", "path": "C:\\Test", "bogus": 1 }
              ]
            }
            """,
            Policy);

        Assert.False(result.IsValid);

        var only = Assert.Single(result.Errors);
        Assert.Equal("/actions/0/bogus", only.Path);
        Assert.Contains("\"bogus\" is not a field on open_path", only.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("false schema", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ExceedingTheNestingLimitSaysSoInsteadOfThreeRollUps()
    {
        // Three messages before — "Some properties did not match the required schema" twice and
        // "Some items do not match" once — none of which mentioned nesting, which is the only way
        // this shape can fail: each of those ifs is individually well-formed.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "if", "id": "a1",
                  "condition": { "type": "process_running", "processName": "x" },
                  "then": [
                    { "type": "if", "id": "a2",
                      "condition": { "type": "process_running", "processName": "y" },
                      "then": [
                        { "type": "if", "id": "a3",
                          "condition": { "type": "process_running", "processName": "z" },
                          "then": [ { "type": "wait", "durationMs": 10 } ] } ] } ] }
              ]
            }
            """,
            Policy);

        Assert.False(result.IsValid);

        var only = Assert.Single(result.Errors);
        Assert.Contains("nest three levels", only.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Some properties did not match", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoLegalLevelsStillValidate()
    {
        // The collapse must not have swallowed the permitted shape.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "if", "id": "a1",
                  "condition": { "type": "process_running", "processName": "x" },
                  "then": [
                    { "type": "if", "id": "a2",
                      "condition": { "type": "process_running", "processName": "y" },
                      "then": [ { "type": "wait", "durationMs": 10 } ] } ] }
              ]
            }
            """,
            Policy);

        Assert.True(result.IsValid, Message(result));
    }

    [Fact]
    public void ARealSpecificErrorIsStillReportedRatherThanBecomingANestingComplaint()
    {
        // The nesting collapse only fires when nothing specific was found anywhere. A plan with a
        // genuine field error must keep getting that error, or the collapse has made things worse.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "move_window", "id": "a1",
                  "selector": { "processName": "notepad" }, "position": "sideways" }
              ]
            }
            """,
            Policy);

        Assert.False(result.IsValid);

        var message = Message(result);
        Assert.DoesNotContain("nest three levels", message, StringComparison.Ordinal);
        Assert.Contains("permitted values", message, StringComparison.Ordinal);
    }

    // ------------------------------- empty plans -------------------------------

    [Fact]
    public void APlanWithNoActionsIsRefused()
    {
        // It validated, was approvable, and bound a global chord that did nothing — taking that key
        // combination away from everything else on the machine, since RegisterHotKey is
        // first-come-first-served process-wide.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "Does nothing",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": []
            }
            """,
            Policy);

        Assert.False(result.IsValid);

        var only = Assert.Single(result.Errors);
        Assert.Equal("/actions", only.Path);
        Assert.Contains("claiming the key combination", only.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBranchInsideAnIfIsStillAllowed()
    {
        // Different question, and the answer is different: an if whose else is empty is an ordinary
        // way to write "only when", and refusing it would be a change nobody asked for.
        var result = PlanValidator.Validate(
            """
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [
                { "type": "if", "id": "a1",
                  "condition": { "type": "process_running", "processName": "x" },
                  "then": [ { "type": "wait", "durationMs": 10 } ],
                  "else": [] }
              ]
            }
            """,
            Policy);

        Assert.True(result.IsValid, Message(result));
    }
}

using HotkeyAI.Core;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// Error <i>quality</i> tests, distinct from "does it reject the right documents".
/// </summary>
/// <remarks>
/// These exist because the validator's first working version emitted 453 errors for a
/// three-mistake plan. It was correct and useless: the actions array is a 25-way union, so a
/// malformed action fails against 24 branches it never claimed to be, and most of the output
/// described constraints the author never invoked. Since these errors are read by a planner in
/// an automated fix loop, precision is a functional requirement — a wall of contradictory
/// advice sends the loop chasing ghosts, and a spurious error is worse than a missing one.
/// </remarks>
public sealed class ValidatorErrorQualityTests
{
    private const string ThreeMistakes = """
        {
          "schemaVersion": 1,
          "name": "Broken On Purpose",
          "trigger": { "type": "hotkey", "keys": ["CTRL", "ALT", "Q"] },
          "actions": [
            { "id": "s1", "type": "launch_process", "app": "vscode",
              "path": "C:\\Windows\\notepad.exe" },
            { "id": "s2", "type": "click_element", "selector": { "name": "OK" } },
            { "id": "s3", "type": "move_window", "selector": { "processName": "Code" },
              "position": "slightly_left" }
          ]
        }
        """;

    [Fact]
    public void ThreeMistakesProduceThreeErrors()
    {
        var result = SchemaValidator.Validate(ThreeMistakes);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void EachMistakeIsReportedAtItsOwnPath()
    {
        var byPath = SchemaValidator.Validate(ThreeMistakes)
            .Errors.ToDictionary(e => e.Path, e => e.Message, StringComparer.Ordinal);

        Assert.Contains("mutually exclusive", byPath["/actions/0"], StringComparison.Ordinal);
        Assert.Contains("click_element", byPath["/actions/1"], StringComparison.Ordinal);
        Assert.Contains("enum", byPath["/actions/2/position"], StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownActionTypeListsTheValidOnes()
    {
        // A planner that guessed wrong needs the answer, not just the rejection.
        var error = Assert.Single(
            SchemaValidator.Validate(ThreeMistakes).Errors,
            e => e.Path == "/actions/1");

        Assert.Contains("launch_process", error.Message, StringComparison.Ordinal);
        Assert.Contains("send_appcommand", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoErrorsFromBranchesTheActionNeverClaimedToBe()
    {
        // The bug this guards: a launch_process being told it is missing `condition`, which
        // only `if` requires, because the union tried the `if` branch.
        foreach (var error in SchemaValidator.Validate(ThreeMistakes).Errors)
        {
            Assert.DoesNotContain("condition", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("itemVariable", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("durationMs", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASatisfiedAnyOfProducesNoErrors()
    {
        // A window selector needs at least one of four fields. Supplying processName satisfies
        // it, but the evaluator still reports the three unused branches as failures. Those must
        // not surface: the selector here is correct, and the only real mistake is `position`.
        var errors = SchemaValidator.Validate(ThreeMistakes).Errors;

        Assert.DoesNotContain(errors, e => e.Path.EndsWith("/selector", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Message.Contains("titleRegex", StringComparison.Ordinal));
    }

    [Fact]
    public void ARealViolationIsNotFilteredAway()
    {
        // Regression: an earlier attempt at reducing noise suppressed the launch_process
        // app/path violation entirely, leaving the plan silently under-reported. Emitting
        // nothing is a worse failure than emitting too much.
        var onlyBothAppAndPath = """
            {
              "schemaVersion": 1,
              "name": "Both",
              "trigger": { "type": "hotkey", "keys": ["CTRL", "ALT", "Q"] },
              "actions": [
                { "type": "launch_process", "app": "vscode", "path": "C:\\Windows\\notepad.exe" }
              ]
            }
            """;

        var result = SchemaValidator.Validate(onlyBothAppAndPath);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ErrorsInsideNestedActionsAreAttributedToTheNestedPath()
    {
        var nestedMistake = """
            {
              "schemaVersion": 1,
              "name": "Nested",
              "trigger": { "type": "hotkey", "keys": ["CTRL", "ALT", "Q"] },
              "actions": [
                {
                  "type": "if",
                  "condition": { "type": "process_running", "processName": "Code" },
                  "then": [ { "type": "notify" } ]
                }
              ]
            }
            """;

        var result = SchemaValidator.Validate(nestedMistake);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Path.StartsWith("/actions/0/then/0", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryErrorIsTaggedWithItsLayer()
    {
        // The two-layer split is only meaningful if consumers can tell them apart; the policy
        // layer lands next and must be distinguishable from schema failures.
        Assert.All(
            SchemaValidator.Validate(ThreeMistakes).Errors,
            e => Assert.Equal(ValidationLayer.Schema, e.Layer));
    }
}

using HotkeyAI.Core;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The validator must reject bad input, never crash on it.
/// </summary>
/// <remarks>
/// Written after the policy layer threw <c>ArgumentException</c> on a plan declaring the same
/// variable twice — a plan it was specifically meant to reject. Building a lookup from
/// attacker- or planner-supplied data crashed the process while assembling the very map used
/// to detect the problem.
/// <para>
/// This matters beyond tidiness. The validator is the gate every plan passes before it can run
/// on a keypress, and it is fed machine-generated content. An unhandled exception is a denial
/// of service at best; at worst a caller that treats a thrown validator as "inconclusive"
/// rather than "invalid" lets an unvalidated plan through.
/// </para>
/// </remarks>
public sealed class ValidatorRobustnessTests
{
    public static TheoryData<string, string> Pathological() => new()
    {
        { "duplicate variable declarations", """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [ { "name": "x", "type": "text" }, { "name": "x", "type": "path" } ],
              "actions": [ { "type": "get_clipboard", "into": "x" } ] }
            """ },
        { "empty action list", """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] }, "actions": [] }
            """ },
        { "empty trigger chord", """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": [] }, "actions": [] }
            """ },
        { "self-referential variable", """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [ { "name": "a", "type": "pathList" }, { "name": "b", "type": "path" } ],
              "actions": [ { "type": "foreach", "source": "a", "itemVariable": "b",
                             "body": [ { "type": "notify", "message": "${a}${b}" } ] } ] }
            """ },
        { "deeply chained interpolation", """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [ { "name": "v", "type": "text" } ],
              "actions": [ { "type": "notify", "message": "${v}${v}${v}${v}${v}${v}${v}" } ] }
            """ },
        { "malformed interpolation syntax", """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [ { "type": "notify", "message": "${ } ${} ${a.b.c} $ { x }" } ] }
            """ },
        { "empty strings everywhere", """
            { "schemaVersion": 1, "name": "",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [ { "type": "notify", "message": "" },
                           { "type": "launch_process", "path": "" } ] }
            """ },
        { "not an object", "[]" },
        { "not JSON at all", "this is not json" },
        { "empty document", "" },
        { "null actions", """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] }, "actions": null }
            """ },
    };

    [Theory]
    [MemberData(nameof(Pathological))]
    public void ValidatorNeverThrows(string description, string json)
    {
        var exception = Record.Exception(() => PlanValidator.Validate(json, PolicyOptions.Default));

        Assert.True(
            exception is null,
            $"Validating \"{description}\" threw {exception?.GetType().Name}: "
            + $"{exception?.Message}. The validator must reject bad input, not crash on it.");
    }

    [Theory]
    [MemberData(nameof(Pathological))]
    public void ValidatorAlwaysReachesAVerdict(string description, string json)
    {
        // A caller must be able to branch on the result. "Threw" is not a verdict.
        var result = PlanValidator.Validate(json, PolicyOptions.Default);

        Assert.NotNull(result);
        Assert.NotNull(result.Errors);

        if (!result.IsValid)
        {
            Assert.NotEmpty(result.Errors);
            Assert.All(result.Errors, e => Assert.False(
                string.IsNullOrWhiteSpace(e.Message),
                $"An error for \"{description}\" carried no message."));
        }
    }

    [Fact]
    public void DuplicateDeclarationIsReportedRatherThanThrown()
    {
        // The specific regression.
        var result = PlanValidator.Validate(
            """
            { "schemaVersion": 1, "name": "n",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [ { "name": "x", "type": "text" }, { "name": "x", "type": "path" } ],
              "actions": [ { "type": "get_clipboard", "into": "x" } ] }
            """,
            PolicyOptions.Default);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Message.Contains("declared more than once", StringComparison.Ordinal));
    }
}

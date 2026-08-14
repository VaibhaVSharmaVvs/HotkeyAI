using HotkeyAI.Core;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The policy layer: everything the schema deliberately cannot express.
/// </summary>
/// <remarks>
/// Each test names the mistake it guards against, because a rule nobody can explain is a rule
/// that gets deleted the first time it is inconvenient.
/// </remarks>
public sealed class PolicyValidatorTests
{
    private static readonly PolicyOptions Options = PolicyOptions.Default with
    {
        AllowedRoots = [@"C:\Users\test\Projects"],
    };

    /// <summary>A plan with one action spliced in, so each test varies exactly one thing.</summary>
    private static string Plan(string actions, string variables = "") =>
        $$"""
        {
          "schemaVersion": 1,
          "name": "Test",
          "trigger": { "type": "hotkey", "keys": ["CTRL", "ALT", "P"] },
          "variables": [{{variables}}],
          "actions": [{{actions}}]
        }
        """;

    private static ValidationResult Check(string json) => PlanValidator.Validate(json, Options);

    private static void AssertPolicyError(ValidationResult result, string fragment)
    {
        Assert.False(result.IsValid, "Expected the plan to be rejected.");
        Assert.All(result.Errors, e => Assert.Equal(ValidationLayer.Policy, e.Layer));
        Assert.Contains(
            result.Errors,
            e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------- numeric bounds -------------------------------

    [Theory]
    [InlineData(@"{ ""type"": ""wait"", ""durationMs"": 999999 }", "10 to 30000")]
    [InlineData(@"{ ""type"": ""wait"", ""durationMs"": 1 }", "10 to 30000")]
    [InlineData(@"{ ""type"": ""notify"", ""message"": ""x"", ""timeoutMs"": 5 }", "100 to 300000")]
    [InlineData(@"{ ""type"": ""send_keys"", ""keys"": [""CTRL"",""S""], ""repeat"": 5000 }", "1 to 50")]
    public void NumericBoundsAreEnforced(string action, string expectedRange)
    {
        // These cannot live in the schema: numeric constraints are outside the subset a
        // constrained decoder can enforce, so the schema states them in descriptions only.
        AssertPolicyError(Check(Plan(action)), expectedRange);
    }

    [Fact]
    public void ListDepthIsCapped()
    {
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""list_directories"", ""path"": ""C:\\Users\\test\\Projects"",
                    ""depth"": 99, ""into"": ""found"" }",
                @"{ ""name"": ""found"", ""type"": ""pathList"" }")),
            "1 to 5");
    }

    // ------------------------------- paths -------------------------------

    [Fact]
    public void PathOutsideAnAllowedRootIsRejected()
    {
        AssertPolicyError(
            Check(Plan(@"{ ""type"": ""launch_process"", ""path"": ""C:\\Windows\\system32\\cmd.exe"" }")),
            "not under an allowed root");
    }

    [Fact]
    public void TraversalOutOfAnAllowedRootIsRejected()
    {
        // The check that matters. A path that textually starts with the allowed root but
        // climbs out of it with .. must not pass.
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""launch_process"",
                    ""path"": ""C:\\Users\\test\\Projects\\..\\..\\..\\Windows\\system32\\cmd.exe"" }")),
            "not under an allowed root");
    }

    [Fact]
    public void ASiblingDirectoryWithASharedPrefixIsNotUnderTheRoot()
    {
        // "C:\Users\test\Projects-Secret" starts with the root as a *string* but is a
        // different directory. Segment-wise comparison is what stops this.
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""launch_process"",
                    ""path"": ""C:\\Users\\test\\Projects-Secret\\tool.exe"" }")),
            "not under an allowed root");
    }

    [Fact]
    public void PathUnderAnAllowedRootIsAccepted()
    {
        Assert.True(
            Check(Plan(
                @"{ ""type"": ""launch_process"",
                    ""path"": ""C:\\Users\\test\\Projects\\build\\tool.exe"" }")).IsValid);
    }

    [Fact]
    public void RelativePathsAreRejected()
    {
        AssertPolicyError(
            Check(Plan(@"{ ""type"": ""launch_process"", ""path"": ""tool.exe"" }")),
            "absolute");
    }

    [Fact]
    public void AnInterpolatedPathIsRefusedWithAnExplanation()
    {
        // Cannot be checked statically. Saying so plainly beats pretending it was validated —
        // the executor has to re-check the resolved value regardless.
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""launch_process"", ""path"": ""${root}\\tool.exe"" }",
                @"{ ""name"": ""root"", ""type"": ""path"" }")),
            "cannot be checked before the plan runs");
    }

    [Fact]
    public void UnknownLogicalAppIsRejectedAndTheKnownOnesListed()
    {
        var result = Check(Plan(@"{ ""type"": ""launch_process"", ""app"": ""emacs"" }"));

        AssertPolicyError(result, "not a known application");
        Assert.Contains(result.Errors, e => e.Message.Contains("vscode", StringComparison.Ordinal));
    }

    // ------------------------------- variables -------------------------------

    [Fact]
    public void UndeclaredVariableReferenceIsRejected()
    {
        AssertPolicyError(
            Check(Plan(@"{ ""type"": ""notify"", ""message"": ""hello ${nobody}"" }")),
            "not declared");
    }

    [Fact]
    public void ReadingBeforeAnythingAssignsIsRejected()
    {
        // The typo case: declared, but nothing ever fills it.
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""notify"", ""message"": ""hello ${later}"" }",
                @"{ ""name"": ""later"", ""type"": ""text"" }")),
            "read before anything assigns it");
    }

    [Fact]
    public void WritingTheWrongTypeIsRejected()
    {
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""list_directories"", ""path"": ""C:\\Users\\test\\Projects"",
                    ""into"": ""result"" }",
                @"{ ""name"": ""result"", ""type"": ""text"" }")),
            "writes a pathlist");
    }

    [Fact]
    public void PickingFromANonListIsRejected()
    {
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""show_picker"", ""source"": ""single"", ""into"": ""chosen"" }",
                @"{ ""name"": ""single"", ""type"": ""text"" },
                  { ""name"": ""chosen"", ""type"": ""text"" }")),
            "needs a pathList or a textList");
    }

    [Fact]
    public void ElementTypeMustMatchTheListItPicksFrom()
    {
        // Picking from a pathList yields a path, not text.
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""list_directories"", ""path"": ""C:\\Users\\test\\Projects"",
                    ""into"": ""dirs"" },
                  { ""type"": ""show_picker"", ""source"": ""dirs"", ""into"": ""chosen"" }",
                @"{ ""name"": ""dirs"", ""type"": ""pathList"" },
                  { ""name"": ""chosen"", ""type"": ""text"" }")),
            "writes a path");
    }

    [Fact]
    public void UnknownPathPropertyIsRejected()
    {
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""list_directories"", ""path"": ""C:\\Users\\test\\Projects"",
                    ""into"": ""dirs"" },
                  { ""type"": ""show_picker"", ""source"": ""dirs"", ""into"": ""chosen"" },
                  { ""type"": ""notify"", ""message"": ""${chosen.sizeOnDisk}"" }",
                @"{ ""name"": ""dirs"", ""type"": ""pathList"" },
                  { ""name"": ""chosen"", ""type"": ""path"" }")),
            "not a readable property");
    }

    [Fact]
    public void PathPropertyOnANonPathIsRejected()
    {
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""get_clipboard"", ""into"": ""copied"" },
                  { ""type"": ""notify"", ""message"": ""${copied.name}"" }",
                @"{ ""name"": ""copied"", ""type"": ""text"" }")),
            "reads a path property");
    }

    [Fact]
    public void DuplicateVariableDeclarationIsRejected()
    {
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""get_clipboard"", ""into"": ""x"" }",
                @"{ ""name"": ""x"", ""type"": ""text"" },
                  { ""name"": ""x"", ""type"": ""text"" }")),
            "declared more than once");
    }

    // ------------------------------- structure -------------------------------

    [Fact]
    public void DuplicateActionIdsAreRejected()
    {
        // Ids identify steps in execution logs and failure reports; ambiguity there makes a
        // repair prompt point at the wrong action.
        AssertPolicyError(
            Check(Plan(
                @"{ ""id"": ""s1"", ""type"": ""notify"", ""message"": ""a"" },
                  { ""id"": ""s1"", ""type"": ""notify"", ""message"": ""b"" }")),
            "used more than once");
    }

    [Fact]
    public void TooManyActionsAreRejected()
    {
        var many = string.Join(",",
            Enumerable.Range(0, 205).Select(i => $$"""{ "type": "notify", "message": "{{i}}" }"""));

        AssertPolicyError(Check(Plan(many)), "the cap is 200");
    }

    [Theory]
    [InlineData(@"[""CTRL"", ""ALT""]", "exactly one non-modifier")]
    [InlineData(@"[""CTRL"", ""ALT"", ""P"", ""Q""]", "exactly one non-modifier")]
    [InlineData(@"[""P""]", "at least one modifier")]
    public void BadTriggerChordsAreRejected(string keys, string fragment)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "name": "Test",
              "trigger": { "type": "hotkey", "keys": {{keys}} },
              "actions": [{ "type": "notify", "message": "x" }]
            }
            """;

        AssertPolicyError(Check(json), fragment);
    }

    [Fact]
    public void SendKeysNeedsExactlyOneNonModifier()
    {
        AssertPolicyError(
            Check(Plan(@"{ ""type"": ""send_keys"", ""keys"": [""CTRL"", ""S"", ""A""] }")),
            "exactly one non-modifier");
    }

    // ------------------------------- layering -------------------------------

    [Fact]
    public void PolicyDoesNotRunUntilTheSchemaPasses()
    {
        // Policy reasons about meaning and assumes a well-formed document. Running it on a
        // malformed one would stack nonsense on top of the real errors.
        var result = Check(Plan(@"{ ""type"": ""not_a_real_action"" }"));

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal(ValidationLayer.Schema, e.Layer));
        Assert.Empty(result.PolicyErrors);
    }

    [Fact]
    public void ConstraintsAreReportedByTheLayerThatOwnsThem()
    {
        // The split is only meaningful if each constraint stays on its own side. A bound
        // migrating into the schema would break V2's constrained generation; one migrating out
        // of policy would stop being enforced at all.
        var schemaProblem = Check(Plan(@"{ ""type"": ""move_window"",
            ""selector"": { ""processName"": ""Code"" }, ""position"": ""sideways"" }"));
        Assert.All(schemaProblem.Errors, e => Assert.Equal(ValidationLayer.Schema, e.Layer));

        var policyProblem = Check(Plan(@"{ ""type"": ""wait"", ""durationMs"": 999999 }"));
        Assert.All(policyProblem.Errors, e => Assert.Equal(ValidationLayer.Policy, e.Layer));
    }

    [Fact]
    public void ErrorMessagesQuoteTypesTheWayTheSchemaSpellsThem()
    {
        // A message saying a variable is declared "pathlist" invites the author to write
        // exactly that, which the schema then rejects — the error would cause the next bug.
        var result = Check(Plan(
            @"{ ""type"": ""list_directories"", ""path"": ""C:\\Users\\test\\Projects"",
                ""into"": ""dirs"" },
              { ""type"": ""show_picker"", ""source"": ""dirs"", ""into"": ""wrong"" }",
            @"{ ""name"": ""dirs"", ""type"": ""pathList"" },
              { ""name"": ""wrong"", ""type"": ""text"" }"));

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.DoesNotContain(
            "pathlist", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryReferenceBearingFieldIsScannedNotJustSomeOfThem()
    {
        // References are found by reflection rather than a per-action switch, precisely so a
        // new primitive cannot silently escape the check. This pins the behaviour: a variable
        // used inside a window selector is as much a reference as one in a message.
        AssertPolicyError(
            Check(Plan(
                @"{ ""type"": ""focus_window"",
                    ""selector"": { ""titleContains"": ""${missing}"" } }")),
            "not declared");
    }
}

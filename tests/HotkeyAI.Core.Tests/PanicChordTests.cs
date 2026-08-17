using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// No automation may take the panic key's chord.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding H4. The rule existed in the dashboard, which refused the
/// chord when it was captured through the UI, and nowhere else — so a hand-authored plan with
/// trigger CTRL+ALT+SHIFT+ESC validated clean. Hand-authored JSON is V1's only real authoring
/// path, so the rule was missing from the one road people use.
/// <para>
/// It lives in <see cref="HotkeyChord.Problems"/> now, which the validator, the CLI and the
/// dashboard all consult, so the three cannot disagree about it again.
/// </para>
/// </remarks>
public sealed class PanicChordTests
{
    private static readonly PolicyOptions Policy = new() { AllowedRoots = [@"C:\Test"] };

    private static string Json(string keys) =>
        $$"""
        {
          "schemaVersion": 1,
          "name": "T",
          "trigger": { "type": "hotkey", "keys": {{keys}} },
          "actions": [{ "type": "wait", "durationMs": 10 }]
        }
        """;

    [Theory]
    [InlineData("""["CTRL", "ALT", "SHIFT", "ESC"]""")]
    [InlineData("""["ESC", "SHIFT", "ALT", "CTRL"]""")]   // order must not matter
    [InlineData("""["ALT", "CTRL", "ESC", "SHIFT"]""")]
    public void APlanCannotBindThePanicChord(string keys)
    {
        var result = PlanValidator.Validate(Json(keys), Policy);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ToString().Contains("panic key", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRefusalSaysWhyItMatters()
    {
        // A user who picked this chord deliberately needs to know what they would be giving up,
        // not just that it is disallowed.
        var problems = HotkeyChord.Problems(HotkeyChord.Panic);

        Assert.Contains(problems, p => p.Contains("stops a running automation", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("abort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""["CTRL", "ALT", "ESC"]""")]            // fewer modifiers
    [InlineData("""["CTRL", "ALT", "SHIFT", "F1"]""")]    // different key
    [InlineData("""["CTRL", "SHIFT", "ESC"]""")]
    public void NeighbouringChordsAreStillAllowed(string keys)
    {
        // The rule has to be exact. Refusing everything near the panic chord would quietly take a
        // block of the keyboard away.
        Assert.True(PlanValidator.Validate(Json(keys), Policy).IsValid);
    }

    [Fact]
    public void TheAgentAndTheValidatorShareOneDefinition()
    {
        // The chord is Core's now, so there is a single list rather than two that match today.
        Assert.True(HotkeyChord.IsPanic([KeyName.Esc, KeyName.Ctrl, KeyName.Shift, KeyName.Alt]));
        Assert.False(HotkeyChord.IsPanic([KeyName.Ctrl, KeyName.Alt, KeyName.Esc]));
    }
}

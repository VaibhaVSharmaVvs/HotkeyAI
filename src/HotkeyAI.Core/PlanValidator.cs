using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core;

/// <summary>
/// Both validation layers, in the only order that works.
/// </summary>
/// <remarks>
/// Schema first, then policy — and policy only if the schema passed. The policy layer reasons
/// about meaning (does this variable exist, is this path allowed) and assumes a well-formed
/// document; running it on a malformed one would produce nonsense on top of the real errors.
/// This is the entry point everything else should use: the CLI, the store, and the agent.
/// </remarks>
public static class PlanValidator
{
    /// <summary>Validate raw JSON through both layers.</summary>
    public static ValidationResult Validate(string json, PolicyOptions? options = null)
    {
        var structural = SchemaValidator.Validate(json);
        if (!structural.IsValid)
        {
            return structural;
        }

        Automation? automation;
        try
        {
            automation = JsonSerializer.Deserialize<Automation>(json, DslJson.Options);
        }
        catch (JsonException ex)
        {
            // Schema-valid but undeserializable means the records and the schema disagree,
            // which the conformance tests exist to prevent. Surface it rather than swallow it.
            return new ValidationResult([
                new ValidationError(
                    ValidationLayer.Schema,
                    "",
                    $"Passed schema validation but could not be read into the object model: "
                    + $"{ex.Message}. This is a defect in Hotkey AI, not in the plan."),
            ]);
        }

        return automation is null
            ? new ValidationResult([
                new ValidationError(ValidationLayer.Schema, "", "The document is empty."),
            ])
            : PolicyValidator.Validate(automation, options);
    }

    /// <summary>Validate an already-parsed plan. Policy layer only.</summary>
    public static ValidationResult Validate(Automation automation, PolicyOptions? options = null) =>
        PolicyValidator.Validate(automation, options);
}

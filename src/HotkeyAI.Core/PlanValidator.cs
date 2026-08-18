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

        // Before deserialising, not after. A number too large for int32 used to throw during
        // deserialisation and be reported as "a defect in Hotkey AI, not in the plan" — blaming the
        // tool for something the plan did, because the schema's "integer" carries no range and
        // nothing else looked. Checked here so the answer is a
        // JSON Pointer at the offending number rather than an STJ message about System.Int32.
        if (OutOfRangeNumbers(json) is { Count: > 0 } tooBig)
        {
            return new ValidationResult(tooBig);
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
                    "Passed schema validation but could not be read into the object model: "
                    + $"{ex.Message} This is a defect in Hotkey AI, not in the plan."),
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

    /// <summary>
    /// Every number in the document that will not fit the <c>int</c> the DSL uses.
    /// </summary>
    /// <remarks>
    /// A walk of the document rather than a reading of the exception, because the exception is a
    /// .NET implementation detail and the pointer is what the user needs. Every numeric in the
    /// schema is an <c>integer</c> mapped to <c>int</c> — there are twelve of them and no floats —
    /// so "does it fit an int" is the whole question, and asking it here means the answer arrives
    /// alongside the other policy errors instead of replacing them.
    /// </remarks>
    private static List<ValidationError> OutOfRangeNumbers(string json)
    {
        var errors = new List<ValidationError>();

        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement, "", errors);
        }
        catch (JsonException)
        {
            // Unparseable JSON is the schema layer's business, and it already ran.
        }

        return errors;

        static void Walk(JsonElement element, string pointer, List<ValidationError> errors)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Walk(property.Value, $"{pointer}/{Escape(property.Name)}", errors);
                    }

                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, $"{pointer}/{index++}", errors);
                    }

                    break;

                case JsonValueKind.Number when !element.TryGetInt32(out _):
                    errors.Add(new ValidationError(
                        ValidationLayer.Policy,
                        pointer,
                        $"{element.GetRawText()} is not a whole number between "
                        + $"{int.MinValue} and {int.MaxValue}. Every number in the DSL is a "
                        + "32-bit integer; check the property's own bounds in "
                        + "docs/capabilities.md, which are narrower still."));
                    break;

                default:
                    break;
            }
        }

        // RFC 6901: "~" and "/" are the two characters a pointer token has to escape.
        static string Escape(string name) =>
            name.Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
    }
}

using System.Text.Json;
using Json.Schema;

namespace HotkeyAI.Core;

/// <summary>Where a rejection came from. Kept distinct because the two layers have
/// different jobs: the schema is what constrains a generating model, the policy layer holds
/// everything the schema cannot express.</summary>
public enum ValidationLayer
{
    /// <summary>Structural — enforced by the JSON Schema, and by a constrained decoder.</summary>
    Schema,

    /// <summary>Semantic — numeric bounds, allowed roots, nesting depth, variable dataflow.</summary>
    Policy,
}

/// <summary>A single reason a plan was rejected.</summary>
/// <param name="Layer">Which layer rejected it.</param>
/// <param name="Path">JSON Pointer to the offending node, e.g. <c>/actions/2/argv</c>.</param>
/// <param name="Message">What is wrong, phrased so it can be fed straight back to a planner.</param>
public sealed record ValidationError(ValidationLayer Layer, string Path, string Message)
{
    public override string ToString() =>
        $"[{Layer.ToString().ToLowerInvariant()}] {(Path.Length == 0 ? "(root)" : Path)}: {Message}";
}

/// <summary>Outcome of validating a plan.</summary>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static readonly ValidationResult Success = new([]);

    public IEnumerable<ValidationError> SchemaErrors =>
        Errors.Where(e => e.Layer == ValidationLayer.Schema);

    public IEnumerable<ValidationError> PolicyErrors =>
        Errors.Where(e => e.Layer == ValidationLayer.Policy);
}

/// <summary>
/// Structural validation against the embedded schema.
/// </summary>
/// <remarks>
/// This is layer one of two. It answers "is this a well-formed plan" — known action types,
/// required fields present, no unknown fields, enums in range. It deliberately does not
/// answer "is this plan sane", because every constraint expressible here must also be
/// expressible to a constrained decoder, and things like numeric bounds are not. Those live
/// in the policy layer.
/// </remarks>
public static class SchemaValidator
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = false,
    };

    /// <summary>Validate raw JSON text.</summary>
    public static ValidationResult Validate(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ValidationResult(
                [new ValidationError(ValidationLayer.Schema, "", $"Not valid JSON: {ex.Message}")]);
        }

        using (document)
        {
            return Validate(document.RootElement);
        }
    }

    /// <summary>Validate a parsed document.</summary>
    public static ValidationResult Validate(JsonElement element)
    {
        var result = DslSchema.Compiled.Evaluate(element, Options);
        if (result.IsValid)
        {
            return ValidationResult.Success;
        }

        var errors = new List<ValidationError>();
        Collect(result, errors);

        // A plan that fails every branch of a oneOf produces a lot of noise. Keep it all,
        // but lead with the shallowest complaint — that is the actionable one.
        return new ValidationResult(
            [.. errors.OrderBy(e => e.Path.Length).ThenBy(e => e.Path, StringComparer.Ordinal)]);
    }

    private static void Collect(EvaluationResults results, List<ValidationError> into)
    {
        if (results.Errors is { Count: > 0 } failures)
        {
            var path = results.InstanceLocation.ToString();
            foreach (var (keyword, message) in failures)
            {
                into.Add(new ValidationError(
                    ValidationLayer.Schema, path, Describe(keyword, message)));
            }
        }

        if (results.Details is null)
        {
            return;
        }

        foreach (var child in results.Details)
        {
            Collect(child, into);
        }
    }

    /// <summary>Turn a schema keyword failure into something a planner can act on.</summary>
    private static string Describe(string keyword, string message) => keyword switch
    {
        "additionalProperties" =>
            $"{message} (unknown fields are rejected — check the action's parameter list)",
        "oneOf" =>
            $"{message} (the object matched no known action type, or matched ambiguously)",
        "required" =>
            $"{message} (a required parameter is missing)",
        _ => message,
    };
}

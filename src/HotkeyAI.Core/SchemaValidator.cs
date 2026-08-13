using System.Globalization;
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
        // Hierarchical, not List. List flattens every subschema the evaluator tried into
        // siblings, so a failing branch of an anyOf that overall *passed* appears as its own
        // node with IsValid=false and gets reported as a real error. Keeping the tree lets us
        // prune whole passing subtrees, which is the only way to tell "this branch did not
        // match" from "this document is wrong".
        OutputFormat = OutputFormat.Hierarchical,
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

        var raw = new List<RawError>();
        Collect(result, raw);

        return new ValidationResult(Refine(raw, element));
    }

    /// <summary>An error before branch noise is filtered out.</summary>
    /// <param name="Keyword">The failing schema keyword.</param>
    /// <param name="InstancePath">JSON Pointer into the document.</param>
    /// <param name="SchemaPath">Which subschema produced it. Carries the branch identity.</param>
    /// <param name="Message">Raw message from the evaluator.</param>
    private readonly record struct RawError(
        string Keyword, string InstancePath, string SchemaPath, string Message);

    /// <summary>
    /// Produce errors that describe the actual mistake instead of union fallout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>actions</c> array is a <c>oneOf</c> over 25 types, so one malformed action fails
    /// against 24 branches it was never meant to match. Evaluating the whole document produced
    /// 453 errors for a three-action plan, most of them absurd — complaining that a
    /// <c>launch_process</c> lacks the <c>condition</c> only <c>if</c> requires. Unusable as a
    /// fix loop, for a person or a planner.
    /// </para>
    /// <para>
    /// So this does not filter the union output. Each action declares what it is, so each is
    /// re-validated against <i>that branch's</i> schema alone. Nested actions get the same
    /// treatment individually, and errors reaching into a nested action's own subtree are left
    /// to that action's pass, so nothing is reported twice and nothing is invented.
    /// </para>
    /// </remarks>
    private static List<ValidationError> Refine(List<RawError> raw, JsonElement root)
    {
        var errors = new List<ValidationError>();

        // Root-level problems (missing name, bad trigger, bad variables) involve no
        // polymorphism, so the union pass reports them cleanly.
        foreach (var error in raw.Where(e => !e.InstancePath.StartsWith("/actions", StringComparison.Ordinal)))
        {
            errors.Add(new ValidationError(
                ValidationLayer.Schema, error.InstancePath, Describe(error.Keyword, error.Message)));
        }

        foreach (var (path, action) in Actions(root, "/actions"))
        {
            errors.AddRange(ValidateAction(path, action));
        }

        var deduped = errors.DistinctBy(e => (e.Path, e.Message)).ToList();

        // Drop "some properties did not match" roll-ups when a specific child error already
        // says which property and why. The summary adds a line without adding information.
        var specific = deduped
            .Where(e => !IsRollUp(e))
            .Select(e => e.Path)
            .ToList();

        return [.. deduped
            .Where(e => !IsRollUp(e)
                        || !specific.Any(p => p.StartsWith(e.Path + "/", StringComparison.Ordinal)))
            .OrderBy(e => e.Path.Length)
            .ThenBy(e => e.Path, StringComparer.Ordinal)];
    }

    private static bool IsRollUp(ValidationError error) =>
        error.Message.StartsWith(
            "Some properties did not match", StringComparison.Ordinal)
        || error.Message.StartsWith(
            "Some items do not match", StringComparison.Ordinal);

    /// <summary>Validate one action against the branch it declares itself to be.</summary>
    private static IEnumerable<ValidationError> ValidateAction(string path, JsonElement action)
    {
        if (!action.TryGetProperty("type", out var marker)
            || marker.ValueKind != JsonValueKind.String)
        {
            return [new ValidationError(
                ValidationLayer.Schema, path,
                "This action has no \"type\". Every action must declare one.")];
        }

        var declared = marker.GetString()!;
        var branch = DslSchema.BranchSchema(declared);

        if (branch is null)
        {
            return [new ValidationError(
                ValidationLayer.Schema, path,
                $"Unknown action type \"{declared}\". Known types: "
                + string.Join(", ", DslSchema.ActionTypes().Order(StringComparer.Ordinal)))];
        }

        var evaluated = branch.Evaluate(action, Options);
        if (evaluated.IsValid)
        {
            return [];
        }

        var raw = new List<RawError>();
        Collect(evaluated, raw);

        var nested = NestedKeys(declared);

        return raw
            // Errors inside a nested action belong to that action's own pass.
            .Where(e => !nested.Any(k =>
                e.InstancePath.StartsWith($"/{k}/", StringComparison.Ordinal)))
            .Select(e => new ValidationError(
                ValidationLayer.Schema,
                path + e.InstancePath,
                Describe(e.Keyword, e.Message)));
    }

    /// <summary>Properties of an action that hold other actions.</summary>
    private static string[] NestedKeys(string discriminator) => discriminator switch
    {
        "if" => ["then", "else"],
        "foreach" => ["body"],
        _ => [],
    };

    /// <summary>Every action in the document, depth-first, with its JSON Pointer.</summary>
    private static IEnumerable<(string Path, JsonElement Action)> Actions(
        JsonElement parent, string arrayPointer)
    {
        var segments = arrayPointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var node = parent;

        foreach (var segment in segments)
        {
            if (node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty(segment, out var child))
            {
                yield break;
            }

            node = child;
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        for (var i = 0; i < node.GetArrayLength(); i++)
        {
            var action = node[i];
            if (action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = $"{arrayPointer}/{i}";
            yield return (path, action);

            var declared = action.TryGetProperty("type", out var m)
                           && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : "";

            foreach (var key in NestedKeys(declared))
            {
                foreach (var child in Actions(action, $"/{key}"))
                {
                    yield return (path + child.Path, child.Action);
                }
            }
        }
    }

    /// <summary>
    /// Walk up from a JSON Pointer to the closest object that declares a <c>type</c>, and
    /// return both that object's path and the declared value.
    /// </summary>
    /// <remarks>
    /// Needed because an error is often reported against a child of the object whose branch
    /// identity we want — a <c>const</c> mismatch on the discriminator itself is reported at
    /// <c>/actions/0/type</c>, while the discriminator that tells us which branch it came from
    /// is a property of <c>/actions/0</c>.
    /// </remarks>
    private static (string Owner, string? Declared) NearestDeclaredType(
        JsonElement root, string pointer)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        for (var take = segments.Count; take >= 0; take--)
        {
            var candidate = "/" + string.Join('/', segments.Take(take));
            var declared = DeclaredType(root, candidate);

            if (declared is not null)
            {
                return (take == 0 ? "" : candidate, declared);
            }
        }

        return (pointer, null);
    }

    /// <summary>Read the <c>type</c> the document declares at a JSON Pointer.</summary>
    private static string? DeclaredType(JsonElement root, string pointer)
    {
        var node = root;

        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = segment.Replace("~1", "/", StringComparison.Ordinal)
                               .Replace("~0", "~", StringComparison.Ordinal);

            switch (node.ValueKind)
            {
                case JsonValueKind.Object when node.TryGetProperty(token, out var child):
                    node = child;
                    break;

                case JsonValueKind.Array when int.TryParse(
                    token, CultureInfo.InvariantCulture, out var i)
                    && i >= 0 && i < node.GetArrayLength():
                    node = node[i];
                    break;

                default:
                    return null;
            }
        }

        return node.ValueKind == JsonValueKind.Object
               && node.TryGetProperty("type", out var marker)
               && marker.ValueKind == JsonValueKind.String
            ? marker.GetString()
            : null;
    }

    private static void Collect(EvaluationResults results, List<RawError> into)
    {
        // Essential with OutputFormat.List: the evaluator reports every subschema it tried,
        // including branches of an anyOf/oneOf that failed while the composite still passed.
        // A window selector supplying processName satisfies its anyOf, yet the three unused
        // branches each report a missing required property. Collecting from a subtree that
        // succeeded invents errors that are not errors.
        if (results.IsValid)
        {
            return;
        }

        if (results.Errors is { Count: > 0 } failures)
        {
            var instancePath = results.InstanceLocation.ToString();
            var schemaPath = results.SchemaLocation.ToString();

            foreach (var (keyword, message) in failures)
            {
                into.Add(new RawError(keyword, instancePath, schemaPath, message));
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

    /// <summary>
    /// Turn a schema keyword failure into something a planner can act on.
    /// </summary>
    /// <remarks>
    /// Raw evaluator messages are written for schema authors, not for whoever has to fix the
    /// document. "All values fail against the false schema" is technically what happened when
    /// <c>additionalProperties: false</c> rejects a field, and it is useless as instruction.
    /// These errors are read by a model in a fix loop, so they have to say what to change.
    /// </remarks>
    private static string Describe(string keyword, string message) => keyword switch
    {
        "additionalProperties" =>
            "This object has fields that do not belong on it. Unknown fields are always "
            + "rejected — check the action's parameter list in docs/capabilities.md.",

        // The subschema for a disallowed property is literally `false`, which is how
        // additionalProperties:false manifests on the offending property itself.
        "false" => "This field is not allowed here.",

        "required" => $"{message} (a required parameter is missing)",

        "enum" => $"{message} (not one of the permitted values)",

        "const" => $"{message} (fixed value for this field)",

        "anyOf" =>
            "None of the permitted field combinations is satisfied. For a window selector, "
            + "supply at least one of processName, titleContains, titleRegex or className.",

        // Mutually-exclusive field groups. launch_process is the case that matters: it must
        // carry exactly one of app or path, so both "none" and "several" are failures and the
        // fix differs.
        "oneOf" when message.Contains("found 0", StringComparison.Ordinal) =>
            "This object satisfies none of the required field combinations — a mandatory field "
            + "is missing (launch_process needs one of app or path).",

        "oneOf" =>
            "This object satisfies more than one mutually exclusive field combination — remove "
            + "one (launch_process must have exactly one of app or path, not both).",

        _ => message,
    };
}

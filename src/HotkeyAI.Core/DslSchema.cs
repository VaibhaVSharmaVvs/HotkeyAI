using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace HotkeyAI.Core;

/// <summary>
/// The DSL contract, embedded at build time.
/// </summary>
/// <remarks>
/// Embedded rather than read from disk so Core always validates against the exact schema it
/// was compiled against, and cannot be pointed at a stale or hand-edited copy at runtime.
/// The file itself still lives once, at <c>schema/hotkeyai-dsl-v1.schema.json</c>; the
/// project links it in.
/// </remarks>
public static class DslSchema
{
    private const string ResourceName = "HotkeyAI.Core.hotkeyai-dsl-v1.schema.json";

    /// <summary>The schema version this build understands.</summary>
    public const int Version = 1;

    private static readonly Lazy<string> LazyText = new(ReadEmbedded, isThreadSafe: true);
    private static readonly Lazy<JsonSchema> LazyCompiled =
        new(() => JsonSchema.FromText(LazyText.Value), isThreadSafe: true);

    /// <summary>Raw schema JSON. This is what a planner should be handed verbatim.</summary>
    public static string Text => LazyText.Value;

    /// <summary>The compiled schema, for structural validation.</summary>
    public static JsonSchema Compiled => LazyCompiled.Value;

    private static string ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{ResourceName}' is missing. Available resources: " +
                string.Join(", ", assembly.GetManifestResourceNames()));

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly Lazy<Dictionary<string, string>> LazyByDefinition =
        new(ReadDiscriminators, isThreadSafe: true);

    /// <summary>
    /// Every action type the schema defines, read out of the schema itself rather than
    /// hard-coded, so the conformance test compares against the real contract.
    /// </summary>
    public static IReadOnlySet<string> ActionTypes() =>
        LazyByDefinition.Value.Values.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Maps each action <c>$defs</c> name to its <c>type</c> discriminator — including the
    /// per-nesting-level control-flow variants, which share a discriminator.
    /// </summary>
    /// <remarks>
    /// Used to attribute a validation error to the branch that raised it, so errors from
    /// branches the author never intended can be discarded.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> DiscriminatorsByDefinition() =>
        LazyByDefinition.Value;

    private static Dictionary<string, string> ReadDiscriminators()
    {
        using var doc = JsonDocument.Parse(Text);
        var defs = doc.RootElement.GetProperty("$defs");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        // Leaf actions are enumerated by the LeafAction union...
        foreach (var branch in defs.GetProperty("LeafAction").GetProperty("oneOf").EnumerateArray())
        {
            var name = branch.GetProperty("$ref").GetString()!.Split('/')[^1];
            map[name] = DiscriminatorOf(defs, name);
        }

        // ...and the control-flow wrappers exist once per nesting level, sharing a
        // discriminator. One CLR type covers all levels; depth is a policy concern.
        foreach (var name in new[] { "IfL0", "IfL1", "ForEachL0", "ForEachL1" })
        {
            map[name] = DiscriminatorOf(defs, name);
        }

        return map;
    }

    private static readonly Lazy<JsonDocument> LazyDocument =
        new(() => JsonDocument.Parse(Text), isThreadSafe: true);

    private static readonly Lazy<Dictionary<string, JsonSchema>> LazyBranchSchemas =
        new(BuildBranchSchemas, isThreadSafe: true);

    /// <summary>
    /// The schema for one action type on its own, so an action can be validated against what
    /// it claims to be instead of against the whole union.
    /// </summary>
    /// <remarks>
    /// Validating an action against the 25-way union means 24 branches fail and produce errors
    /// describing constraints the author never invoked. Entering at the declared branch instead
    /// yields only real errors — no filtering heuristics, no guessing which failures were
    /// meant. Control flow resolves to the outermost (L0) variant so nesting stays permitted.
    /// </remarks>
    /// <param name="discriminator">An action <c>type</c> value.</param>
    /// <returns>The branch schema, or null if the type is unknown.</returns>
    public static JsonSchema? BranchSchema(string discriminator) =>
        LazyBranchSchemas.Value.GetValueOrDefault(discriminator);

    private static Dictionary<string, JsonSchema> BuildBranchSchemas()
    {
        // Register the main document so the $ref in each wrapper resolves against it, giving
        // the branch schema access to every shared definition.
        SchemaRegistry.Global.Register(Compiled);

        var id = Compiled.BaseUri?.ToString()
            ?? throw new InvalidOperationException("The schema has no $id to reference.");

        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);

        foreach (var (definition, discriminator) in LazyByDefinition.Value)
        {
            // Prefer the L0 variant of a control-flow action: its nesting allowance is the
            // widest, and depth is enforced by the policy layer anyway.
            if (schemas.ContainsKey(discriminator) && !definition.EndsWith("L0", StringComparison.Ordinal))
            {
                continue;
            }

            schemas[discriminator] = JsonSchema.FromText(
                $"{{\"$ref\": \"{id}#/$defs/{definition}\"}}");
        }

        return schemas;
    }

    private static readonly Dictionary<string, string?> BranchCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Given a schema location, find which action branch it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed to tell a real error from <c>oneOf</c> branch noise. A malformed action is
    /// evaluated against every branch of the union, so most of the resulting errors come from
    /// branches the author never intended — and the only way to know which is which is to ask
    /// what schema produced each error.
    /// </para>
    /// <para>
    /// String-matching <c>$defs/&lt;name&gt;</c> is not enough: a location may route through
    /// the referencing path, e.g. <c>#/$defs/ActionL0/oneOf/0/oneOf/6/properties/selector</c>,
    /// naming no action definition at all. So this walks the pointer through the document,
    /// following local <c>$ref</c>s, and reports the discriminator of the innermost enclosing
    /// schema that pins <c>type</c> to a constant.
    /// </para>
    /// </remarks>
    /// <param name="schemaLocation">A schema URI or pointer, with or without a fragment.</param>
    /// <returns>The branch's <c>type</c> value, or null if the location is not inside one.</returns>
    public static string? BranchDiscriminatorAt(string schemaLocation)
    {
        ArgumentNullException.ThrowIfNull(schemaLocation);

        lock (BranchCache)
        {
            if (BranchCache.TryGetValue(schemaLocation, out var cached))
            {
                return cached;
            }

            var resolved = Resolve(schemaLocation);
            BranchCache[schemaLocation] = resolved;
            return resolved;
        }
    }

    private static string? Resolve(string schemaLocation)
    {
        var hash = schemaLocation.IndexOf('#', StringComparison.Ordinal);
        var pointer = hash >= 0 ? schemaLocation[(hash + 1)..] : schemaLocation;

        var node = LazyDocument.Value.RootElement;
        string? innermost = DiscriminatorAt(node);

        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Uri.UnescapeDataString(segment)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (!Step(ref node, token))
            {
                break;
            }

            // Follow a $ref so a location expressed through the referencing path still lands
            // on the definition that actually constrains the value.
            if (node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("$ref", out var reference)
                && reference.ValueKind == JsonValueKind.String
                && Dereference(reference.GetString()!) is { } target)
            {
                node = target;
            }

            innermost = DiscriminatorAt(node) ?? innermost;
        }

        return innermost;
    }

    private static bool Step(ref JsonElement node, string token)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object when node.TryGetProperty(token, out var child):
                node = child;
                return true;

            case JsonValueKind.Array
                when int.TryParse(token, CultureInfo.InvariantCulture, out var index)
                     && index >= 0 && index < node.GetArrayLength():
                node = node[index];
                return true;

            default:
                return false;
        }
    }

    private static JsonElement? Dereference(string reference)
    {
        var hash = reference.IndexOf('#', StringComparison.Ordinal);
        var pointer = hash >= 0 ? reference[(hash + 1)..] : reference;

        var node = LazyDocument.Value.RootElement;

        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Step(ref node, segment))
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>The <c>type</c> constant a schema object pins, if it pins one.</summary>
    private static string? DiscriminatorAt(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.Object
        && type.TryGetProperty("const", out var constant)
        && constant.ValueKind == JsonValueKind.String
            ? constant.GetString()
            : null;

    private static string DiscriminatorOf(JsonElement defs, string defName) =>
        defs.GetProperty(defName)
            .GetProperty("properties")
            .GetProperty("type")
            .GetProperty("const")
            .GetString()!;
}

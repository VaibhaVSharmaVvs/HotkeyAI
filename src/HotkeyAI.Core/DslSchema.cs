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

    /// <summary>
    /// Every action type the schema defines, read out of the schema itself rather than
    /// hard-coded, so the conformance test compares against the real contract.
    /// </summary>
    public static IReadOnlySet<string> ActionTypes()
    {
        using var doc = JsonDocument.Parse(Text);
        var defs = doc.RootElement.GetProperty("$defs");

        var names = new HashSet<string>(StringComparer.Ordinal);

        // Leaf actions are enumerated by the LeafAction union...
        foreach (var branch in defs.GetProperty("LeafAction").GetProperty("oneOf").EnumerateArray())
        {
            var defName = branch.GetProperty("$ref").GetString()!.Split('/')[^1];
            names.Add(DiscriminatorOf(defs, defName));
        }

        // ...and the control-flow wrappers exist once per nesting level, sharing a
        // discriminator. One CLR type covers all levels; depth is a policy concern.
        foreach (var defName in new[] { "IfL0", "ForEachL0" })
        {
            names.Add(DiscriminatorOf(defs, defName));
        }

        return names;
    }

    private static string DiscriminatorOf(JsonElement defs, string defName) =>
        defs.GetProperty(defName)
            .GetProperty("properties")
            .GetProperty("type")
            .GetProperty("const")
            .GetString()!;
}

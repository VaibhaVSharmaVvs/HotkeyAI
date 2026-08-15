using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;

using HotkeyAI.Tests;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The C# records and the schema must agree. The schema is the source of truth, so every
/// disagreement is a bug in the records — but the test is bidirectional so that neither
/// adding a record without a schema entry, nor adding a schema entry without a record,
/// can pass silently.
/// </summary>
public sealed class SchemaConformanceTests
{
    /// <summary>Discriminators declared on the <see cref="HotkeyAction"/> hierarchy.</summary>
    private static HashSet<string> RecordDiscriminators() =>
        typeof(HotkeyAction)
            .GetCustomAttributes(typeof(DslTypeAttribute), inherit: false)
            .Cast<DslTypeAttribute>()
            .Select(a => a.Discriminator)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EverySchemaActionTypeHasARecord()
    {
        var missing = DslSchema.ActionTypes().Except(RecordDiscriminators()).OrderBy(x => x);

        Assert.True(
            !missing.Any(),
            "The schema defines action types with no corresponding C# record. Add a record and "
            + "a [JsonDerivedType] entry on HotkeyAction for: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryRecordDiscriminatorExistsInTheSchema()
    {
        var extra = RecordDiscriminators().Except(DslSchema.ActionTypes()).OrderBy(x => x);

        Assert.True(
            !extra.Any(),
            "C# declares action types the schema does not. The schema is the contract, so "
            + "either add them to it or remove the records: " + string.Join(", ", extra));
    }

    [Fact]
    public void ActionCountMatchesTheDocumentedPrimitiveSet()
    {
        // Guards against a primitive being added to both sides but not to docs/capabilities.md
        // or the examples — those have their own gates, but a surprising count here is the
        // first sign something grew without a decision.
        Assert.Equal(25, DslSchema.ActionTypes().Count);
        Assert.Equal(25, RecordDiscriminators().Count);
    }

    [Fact]
    public void EmbeddedSchemaMatchesTheFileOnDisk()
    {
        // The csproj links the repo-root schema rather than copying it. If that link ever
        // breaks, Core would validate against a stale contract while the Python checks
        // validate against the real one — a silent, nasty divergence.
        var onDisk = File.ReadAllText(RepoPaths.Schema);

        Assert.Equal(
            onDisk.ReplaceLineEndings("\n").Trim(),
            DslSchema.Text.ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void SchemaHasNoRefCycles()
    {
        // Structured outputs reject recursive schemas, so a cycle here would block the V2
        // planner. Asserted in V1 so it cannot regress unnoticed.
        using var doc = JsonDocument.Parse(DslSchema.Text);
        var defs = doc.RootElement.GetProperty("$defs");

        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var def in defs.EnumerateObject())
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            CollectRefs(def.Value, targets);
            edges[def.Name] = targets;
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in edges.Keys)
        {
            var cycle = FindCycle(node, edges, state, []);
            Assert.True(cycle is null, $"$ref cycle in the schema: {cycle}");
        }
    }

    private static void CollectRefs(JsonElement node, HashSet<string> into)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("$ref") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString()!;
                        if (value.StartsWith("#/$defs/", StringComparison.Ordinal))
                        {
                            into.Add(value["#/$defs/".Length..].Split('/')[0]);
                        }
                    }
                    else
                    {
                        CollectRefs(prop.Value, into);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    CollectRefs(item, into);
                }

                break;
        }
    }

    private const int Visiting = 1;
    private const int Done = 2;

    private static string? FindCycle(
        string node,
        Dictionary<string, HashSet<string>> edges,
        Dictionary<string, int> state,
        List<string> stack)
    {
        if (state.TryGetValue(node, out var s))
        {
            return s == Visiting ? string.Join(" -> ", [.. stack, node]) : null;
        }

        state[node] = Visiting;
        stack.Add(node);

        foreach (var next in edges.GetValueOrDefault(node, []))
        {
            var cycle = FindCycle(next, edges, state, stack);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        stack.RemoveAt(stack.Count - 1);
        state[node] = Done;
        return null;
    }
}

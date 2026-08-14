using System.Text.Json;
using System.Text.RegularExpressions;
using HotkeyAI.Core;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The policy layer's rules are duplicated in the schema as prose, and the copies must agree.
/// </summary>
/// <remarks>
/// The schema cannot express numeric bounds or the app registry, so it states them in
/// <c>description</c> text instead — and those descriptions are prompt material a planner
/// reads. Documentation that has drifted from enforcement is worse than none: it teaches a
/// planner to produce plans that pass generation and then fail validation for reasons the
/// contract said were fine.
/// </remarks>
public sealed partial class PolicyDocumentationTests
{
    [GeneratedRegex(@"Policy bound:\s*(-?\d+)\s*to\s*(-?\d+)")]
    private static partial Regex BoundClause { get; }

    [GeneratedRegex(@"Known values include:\s*([^.]+)\.")]
    private static partial Regex KnownAppsClause { get; }

    /// <summary>Every <c>description</c> string in the schema, with its JSON path.</summary>
    private static List<(string Path, string Text)> Descriptions()
    {
        using var document = JsonDocument.Parse(DslSchema.Text);
        return Collect(document.RootElement, "#").ToList();

        static IEnumerable<(string, string)> Collect(JsonElement node, string path)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject())
                    {
                        if (property.NameEquals("description")
                            && property.Value.ValueKind == JsonValueKind.String)
                        {
                            yield return (path, property.Value.GetString()!);
                        }
                        else
                        {
                            foreach (var found in Collect(property.Value, $"{path}/{property.Name}"))
                            {
                                yield return found;
                            }
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in node.EnumerateArray())
                    {
                        foreach (var found in Collect(item, $"{path}/{index++}"))
                        {
                            yield return found;
                        }
                    }

                    break;
            }
        }
    }

    [Fact]
    public void EveryDocumentedBoundIsActuallyEnforced()
    {
        var options = PolicyOptions.Default;

        var enforced = new HashSet<(int, int)>
        {
            (options.Timeout.Min, options.Timeout.Max),
            (options.Within.Min, options.Within.Max),
            (options.WaitDuration.Min, options.WaitDuration.Max),
            (options.ListDepth.Min, options.ListDepth.Max),
            (options.Iterations.Min, options.Iterations.Max),
            (options.KeyRepeat.Min, options.KeyRepeat.Max),
        };

        var documented = Descriptions()
            .SelectMany(d => BoundClause.Matches(d.Text).Select(m => (Location: d.Path, Match: m)))
            .ToList();

        Assert.NotEmpty(documented);

        foreach (var (location, match) in documented)
        {
            var pair = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));

            Assert.True(
                enforced.Contains(pair),
                $"The schema documents a policy bound of {pair.Item1} to {pair.Item2} at "
                + $"{location}, but PolicyOptions enforces no such range. A planner reads that "
                + "description and will emit values the validator then rejects.");
        }
    }

    [Fact]
    public void EveryEnforcedBoundIsDocumented()
    {
        // The other direction: a bound enforced but never stated is a rule a planner cannot
        // know about, so it discovers it only by being rejected.
        var options = PolicyOptions.Default;
        var documented = Descriptions()
            .SelectMany(d => BoundClause.Matches(d.Text))
            .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
            .ToHashSet();

        var expected = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["timeoutMs"] = (options.Timeout.Min, options.Timeout.Max),
            ["withinMs"] = (options.Within.Min, options.Within.Max),
            ["durationMs"] = (options.WaitDuration.Min, options.WaitDuration.Max),
            ["depth"] = (options.ListDepth.Min, options.ListDepth.Max),
            ["maxIterations"] = (options.Iterations.Min, options.Iterations.Max),
            ["repeat"] = (options.KeyRepeat.Min, options.KeyRepeat.Max),
        };

        foreach (var (field, range) in expected)
        {
            Assert.True(
                documented.Contains(range),
                $"PolicyOptions enforces {range.Item1} to {range.Item2} for {field}, but no "
                + "schema description states it. Add \"Policy bound: X to Y.\" to that "
                + "property's description so a planner learns the limit.");
        }
    }

    [Fact]
    public void TheSchemaListsExactlyTheAppsTheRegistryKnows()
    {
        var description = Descriptions()
            .Select(d => d.Text)
            .FirstOrDefault(t => KnownAppsClause.IsMatch(t));

        Assert.NotNull(description);

        var listed = KnownAppsClause.Match(description).Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var known = AppRegistry.KnownApps;

        Assert.True(
            listed.SetEquals(known),
            "The schema's launch_process `app` description and AppRegistry.KnownApps disagree.\n"
            + $"  documented but not resolvable: {string.Join(", ", listed.Except(known).Order())}\n"
            + $"  resolvable but undocumented:  {string.Join(", ", known.Except(listed).Order())}\n"
            + "The first kind is worse: it tells a planner an app is available when launching "
            + "it will fail.");
    }

    [Fact]
    public void TheNestingCapMatchesTheSchemasBoundedLevels()
    {
        // The schema encodes the cap structurally as ActionL0 -> ActionL1 -> ActionL2, which is
        // what keeps it non-recursive for V2. Policy re-checks it, and the two must agree.
        using var document = JsonDocument.Parse(DslSchema.Text);
        var defs = document.RootElement.GetProperty("$defs");

        var levels = defs.EnumerateObject()
            .Count(p => p.Name.StartsWith("ActionL", StringComparison.Ordinal));

        Assert.Equal(PolicyOptions.Default.MaxNestingDepth, levels);
    }
}

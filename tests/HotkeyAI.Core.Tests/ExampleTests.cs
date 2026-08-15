using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;

using HotkeyAI.Tests;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The reference automations are the corpus. They must validate against the schema and
/// round-trip through the records — name-level conformance is not enough, because a record
/// whose shape is wrong still deserializes into the wrong thing.
/// </summary>
public sealed class ExampleTests
{
    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var file in RepoPaths.ExampleFiles())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    // Deliberately the shipping options, not test-local ones — if the canonical settings
    // produce documents that fail validation, that is a real defect and the tests must see it.
    private static readonly JsonSerializerOptions Options = DslJson.Options;

    [Theory]
    [MemberData(nameof(Examples))]
    public void ExampleValidatesAgainstSchema(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(RepoPaths.Examples, fileName));

        var result = SchemaValidator.Validate(json);

        Assert.True(
            result.IsValid,
            $"{fileName} failed schema validation:{Environment.NewLine}"
            + string.Join(Environment.NewLine, result.Errors.Take(10)));
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void ExampleDeserializesIntoRecords(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(RepoPaths.Examples, fileName));

        var automation = JsonSerializer.Deserialize<Automation>(json, Options);

        Assert.NotNull(automation);
        Assert.Equal(DslSchema.Version, automation.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(automation.Name));
        Assert.NotEmpty(automation.Actions);

        // A silently-wrong polymorphic setup shows up here: every action must land on a
        // concrete type, never the abstract base.
        foreach (var action in Flatten(automation.Actions))
        {
            Assert.False(
                action.GetType() == typeof(HotkeyAction),
                "An action deserialized to the abstract base type.");
        }
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void ExampleSurvivesARoundTrip(string fileName)
    {
        // Deserialize -> serialize -> validate. If the records drop or rename a field, the
        // re-serialized document stops matching the schema, which is the cheapest way to
        // catch a modelling mistake that name-level conformance cannot see.
        var json = File.ReadAllText(Path.Combine(RepoPaths.Examples, fileName));

        var automation = JsonSerializer.Deserialize<Automation>(json, Options)!;
        var reserialized = JsonSerializer.Serialize(automation, Options);

        var result = SchemaValidator.Validate(reserialized);

        Assert.True(
            result.IsValid,
            $"{fileName} no longer validates after a round trip through the records — a field "
            + $"is being dropped or renamed:{Environment.NewLine}"
            + string.Join(Environment.NewLine, result.Errors.Take(10)));
    }

    private static IEnumerable<HotkeyAction> Flatten(IEnumerable<HotkeyAction> actions)
    {
        foreach (var action in actions)
        {
            yield return action;

            switch (action)
            {
                case IfAction ifAction:
                    foreach (var nested in Flatten(ifAction.Then.Concat(ifAction.Else)))
                    {
                        yield return nested;
                    }

                    break;

                case ForEachAction forEach:
                    foreach (var nested in Flatten(forEach.Body))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }
}

using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Core.Policy;
using HotkeyAI.Tests;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The regression corpus: plans that must survive every change to the DSL.
/// </summary>
/// <remarks>
/// The eight reference automations are the first-run set and are chosen to be readable. This is
/// the opposite: fifty-odd plans chosen to be <i>exhaustive</i>, so that a change to the schema,
/// the validator or the renderer cannot quietly alter what an existing automation means.
/// <para>
/// In V1 this guards against refactors. In V2 the same corpus becomes the planner's evaluation
/// set once expected-output pairs are added, which is why it is worth building before there is a
/// planner to evaluate — a baseline collected afterwards is a baseline of whatever the planner
/// already does.
/// </para>
/// </remarks>
public sealed class CorpusTests
{
    /// <summary>
    /// A fixed, fictional allowed root.
    /// </summary>
    /// <remarks>
    /// Not the current user's profile. The corpus has to validate identically on a contributor's
    /// laptop and on Linux CI, and a policy that depends on who is running it would make failures
    /// depend on that too.
    /// </remarks>
    private static readonly PolicyOptions Policy = PolicyOptions.Default with
    {
        AllowedRoots = [@"C:\Corpus"],
    };

    public static TheoryData<string> Plans()
    {
        var data = new TheoryData<string>();

        foreach (var file in RepoPaths.CorpusFiles())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void EveryPlanIsValid(string fileName)
    {
        var result = PlanValidator.Validate(Read(fileName), Policy);

        Assert.True(
            result.IsValid,
            $"{fileName} no longer validates:{Environment.NewLine}"
            + string.Join(Environment.NewLine, result.Errors.Select(e => $"  {e}")));
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void EveryPlanRoundTripsThroughJson(string fileName)
    {
        // Deserialise, serialise, deserialise. A discriminator or converter change that loses a
        // property survives a single parse and shows up here.
        var original = Deserialise(fileName);
        var again = JsonSerializer.Serialize(original, DslJson.Options);
        var round = JsonSerializer.Deserialize<Automation>(again, DslJson.Options);

        Assert.NotNull(round);
        Assert.Equal(
            JsonSerializer.Serialize(original, DslJson.Options),
            JsonSerializer.Serialize(round, DslJson.Options));
    }

    /// <summary>
    /// The rendered preview of every plan, pinned.
    /// </summary>
    /// <remarks>
    /// This is the test that earns the corpus its keep. The renderer is what the user reads before
    /// approving, so a change that makes a plan <i>describe</i> itself differently is a change to
    /// the only thing standing between a file and a keypress — and unlike a validation failure, it
    /// is completely silent. Golden files make it loud.
    /// <para>
    /// Set <c>HOTKEYAI_UPDATE_GOLDENS=1</c> to rewrite them after a deliberate change, then read
    /// the diff. Reading the diff is the point; regenerating without looking defeats the test.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Plans))]
    public void EveryPlanRendersAsItDidBefore(string fileName)
    {
        var rendered = PlanRenderer.Explain(Deserialise(fileName)).ReplaceLineEndings("\n");
        var golden = Path.Combine(
            RepoPaths.CorpusRendered, Path.ChangeExtension(fileName, ".txt"));

        if (Environment.GetEnvironmentVariable("HOTKEYAI_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(RepoPaths.CorpusRendered);
            File.WriteAllText(golden, rendered);
            return;
        }

        Assert.True(File.Exists(golden), $"No golden render for {fileName}. Generate it with "
            + "HOTKEYAI_UPDATE_GOLDENS=1 and read the diff before committing.");

        Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), rendered);
    }

    // ------------------------------- coverage -------------------------------

    /// <summary>
    /// Every action type appears somewhere in the corpus.
    /// </summary>
    /// <remarks>
    /// A hard gate, like the one over the examples. A primitive with no corpus plan is a primitive
    /// whose rendering and validation nothing pins, and the gap would only be discovered by the
    /// change that broke it.
    /// </remarks>
    [Fact]
    public void EveryActionTypeIsExercised()
    {
        // Discriminators come from the same attributes the conformance test reads, so this cannot
        // drift from what the DSL actually declares.
        var declared = typeof(HotkeyAction)
            .GetCustomAttributes(typeof(DslTypeAttribute), inherit: false)
            .Cast<DslTypeAttribute>()
            .ToDictionary(a => a.DerivedType, a => a.Discriminator);

        var seen = AllActions()
            .Select(a => declared.GetValueOrDefault(a.GetType()))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var missing = declared.Values.Except(seen).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            "The corpus does not exercise: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryPostconditionTypeIsExercised()
    {
        var seen = AllActions()
            .OfType<VerifiableAction>()
            .Select(a => a.Expect)
            .OfType<Postcondition>()
            .Select(p => p.GetType().Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void EveryPredicateTypeIsExercised()
    {
        var seen = AllActions()
            .OfType<IfAction>()
            .SelectMany(Predicates)
            .Select(p => p.GetType().Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void BothCompositeConditionsAreExercised()
    {
        var conditions = AllActions().OfType<IfAction>().Select(i => i.Condition).ToList();

        Assert.Contains(conditions, c => c is AllOfCondition);
        Assert.Contains(conditions, c => c is AnyOfCondition);
    }

    [Fact]
    public void EveryVariableTypeThatCanBeWrittenIsExercised()
    {
        var declared = AllPlans().SelectMany(p => p.Variables).Select(v => v.Type).ToHashSet();

        // textList and integer have no primitive that produces them yet, which is itself worth
        // pinning: if one is added, this test is the reminder to give it corpus coverage.
        Assert.Contains(VariableType.Text, declared);
        Assert.Contains(VariableType.Path, declared);
        Assert.Contains(VariableType.PathList, declared);
        Assert.Contains(VariableType.Boolean, declared);
    }

    [Fact]
    public void TheCorpusIsBigEnoughToBeWorthHaving()
    {
        // PLAN.md asks for 40 to 60. The lower bound is the point: a handful of plans cannot
        // cover the combinations, and the upper bound keeps it readable by a person.
        var count = RepoPaths.CorpusFiles().Count();

        Assert.InRange(count, 40, 60);
    }

    [Fact]
    public void EveryPlanHasADistinctTriggerAndName()
    {
        // Not required by the schema — these are separate files — but a corpus with duplicates is
        // one where a plan was copied and then not edited.
        var plans = AllPlans().ToList();

        Assert.Distinct(plans.Select(p => string.Join('+', p.Trigger.Keys)));
        Assert.Distinct(plans.Select(p => p.Name));
    }

    // ---------------------------------------------------------------------------------

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(RepoPaths.CorpusPlans, fileName));

    private static Automation Deserialise(string fileName) =>
        JsonSerializer.Deserialize<Automation>(Read(fileName), DslJson.Options)
        ?? throw new InvalidOperationException($"{fileName} did not deserialise.");

    private static IEnumerable<Automation> AllPlans() =>
        RepoPaths.CorpusFiles().Select(f => Deserialise(Path.GetFileName(f)));

    private static IEnumerable<HotkeyAction> AllActions() =>
        AllPlans().SelectMany(p => Flatten(p.Actions));

    private static IEnumerable<HotkeyAction> Flatten(IEnumerable<HotkeyAction> actions)
    {
        foreach (var action in actions)
        {
            yield return action;

            var children = action switch
            {
                IfAction i => i.Then.Concat(i.Else ?? []),
                ForEachAction f => f.Body,
                _ => [],
            };

            foreach (var child in Flatten(children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<SimplePredicate> Predicates(IfAction action) => action.Condition switch
    {
        SimplePredicate p => [p],
        AllOfCondition all => all.Conditions,
        AnyOfCondition any => any.Conditions,
        _ => [],
    };
}

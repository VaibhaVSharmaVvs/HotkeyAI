namespace HotkeyAI.Core.Policy;

/// <summary>
/// The bounds the policy layer enforces.
/// </summary>
/// <remarks>
/// These exist here rather than in the schema because the schema is kept inside the subset a
/// constrained decoder can enforce, which excludes numeric bounds and anything needing runtime
/// configuration. The numbers are repeated in the schema's property descriptions so a planner
/// still learns them; <c>PolicyBoundsMatchSchemaTests</c> asserts the two agree.
/// </remarks>
public sealed record PolicyOptions
{
    /// <summary>Absolute Windows paths a plan may launch or read beneath.</summary>
    /// <remarks>
    /// Empty means "no path is allowed", which is the safe default: a plan must then use a
    /// logical <c>app</c> name, whose resolution the engine controls.
    /// </remarks>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>Logical application names that resolve.</summary>
    public IReadOnlySet<string> KnownApps { get; init; } = AppRegistry.KnownApps;

    /// <summary>Cap on total actions, nested ones included.</summary>
    public int MaxActions { get; init; } = 200;

    /// <summary>Action nesting levels permitted. Three: L0, L1, then leaves only.</summary>
    public int MaxNestingDepth { get; init; } = 3;

    /// <summary>Bounds for <c>timeoutMs</c>.</summary>
    public Range<int> Timeout { get; init; } = new(100, 300_000);

    /// <summary>Bounds for a postcondition's <c>withinMs</c>.</summary>
    public Range<int> Within { get; init; } = new(100, 120_000);

    /// <summary>Bounds for <c>wait.durationMs</c>.</summary>
    public Range<int> WaitDuration { get; init; } = new(10, 30_000);

    /// <summary>Bounds for directory and file listing <c>depth</c>.</summary>
    public Range<int> ListDepth { get; init; } = new(1, 5);

    /// <summary>Bounds for <c>foreach.maxIterations</c>.</summary>
    public Range<int> Iterations { get; init; } = new(1, 100);

    /// <summary>Bounds for <c>send_keys.repeat</c>.</summary>
    public Range<int> KeyRepeat { get; init; } = new(1, 50);

    /// <summary>Defaults, with no allowed roots.</summary>
    public static PolicyOptions Default { get; } = new();
}

/// <summary>An inclusive numeric range.</summary>
/// <param name="Min">Lowest permitted value.</param>
/// <param name="Max">Highest permitted value.</param>
public readonly record struct Range<T>(T Min, T Max)
    where T : IComparable<T>
{
    /// <summary>True if the value falls within the range.</summary>
    public bool Contains(T value) =>
        value.CompareTo(Min) >= 0 && value.CompareTo(Max) <= 0;

    public override string ToString() => $"{Min} to {Max}";
}

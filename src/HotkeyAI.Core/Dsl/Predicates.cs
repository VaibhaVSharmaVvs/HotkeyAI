using System.Text.Json.Serialization;
using HotkeyAI.Core.Json;

namespace HotkeyAI.Core.Dsl;

/// <summary>Identifies a target window. At least one field must be set; when several are
/// set, all must match.</summary>
public sealed record WindowSelector
{
    [JsonPropertyName("processName")]
    public string? ProcessName { get; init; }

    [JsonPropertyName("titleContains")]
    public string? TitleContains { get; init; }

    [JsonPropertyName("titleRegex")]
    public string? TitleRegex { get; init; }

    [JsonPropertyName("className")]
    public string? ClassName { get; init; }
}

// ---------------------------------------------------------------------------------------
// Postconditions â€” the only five checks that are machine-verifiable. An action without one
// is reported to the user as unverified rather than silently assumed to have worked.
// ---------------------------------------------------------------------------------------

[JsonConverter(typeof(DiscriminatedJsonConverter<Postcondition>))]
[DslType(typeof(ProcessRunningExpectation), "process_running")]
[DslType(typeof(WindowExistsExpectation), "window_exists")]
[DslType(typeof(PathExistsExpectation), "path_exists")]
[DslType(typeof(ClipboardMatchesExpectation), "clipboard_matches")]
[DslType(typeof(ForegroundProcessIsExpectation), "foreground_process_is")]
public abstract record Postcondition
{
    /// <summary>How long to poll before declaring failure. Bounds are a policy concern.</summary>
    [JsonPropertyName("withinMs")]
    public int? WithinMs { get; init; }
}

public sealed record ProcessRunningExpectation : Postcondition
{
    [JsonPropertyName("processName")] public required string ProcessName { get; init; }
}

public sealed record WindowExistsExpectation : Postcondition
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
}

public sealed record PathExistsExpectation : Postcondition
{
    [JsonPropertyName("path")] public required string Path { get; init; }
}

public sealed record ClipboardMatchesExpectation : Postcondition
{
    [JsonPropertyName("contains")] public string? Contains { get; init; }

    /// <summary>Wire name is <c>equals</c>; renamed here only because a record already
    /// generates an <c>Equals</c> member.</summary>
    [JsonPropertyName("equals")] public string? Exactly { get; init; }
}

public sealed record ForegroundProcessIsExpectation : Postcondition
{
    [JsonPropertyName("processName")] public required string ProcessName { get; init; }
}

// ---------------------------------------------------------------------------------------
// Conditions. SimplePredicate is a Condition so `if` can take either directly, while
// all_of/any_of hold only SimplePredicates â€” conditions deliberately do not nest further.
// ---------------------------------------------------------------------------------------

[JsonConverter(typeof(DiscriminatedJsonConverter<Condition>))]
[DslType(typeof(ProcessRunningPredicate), "process_running")]
[DslType(typeof(WindowExistsPredicate), "window_exists")]
[DslType(typeof(PathExistsPredicate), "path_exists")]
[DslType(typeof(VariableEqualsPredicate), "variable_equals")]
[DslType(typeof(VariableEmptyPredicate), "variable_empty")]
[DslType(typeof(AllOfCondition), "all_of")]
[DslType(typeof(AnyOfCondition), "any_of")]
public abstract record Condition;

[JsonConverter(typeof(DiscriminatedJsonConverter<SimplePredicate>))]
[DslType(typeof(ProcessRunningPredicate), "process_running")]
[DslType(typeof(WindowExistsPredicate), "window_exists")]
[DslType(typeof(PathExistsPredicate), "path_exists")]
[DslType(typeof(VariableEqualsPredicate), "variable_equals")]
[DslType(typeof(VariableEmptyPredicate), "variable_empty")]
public abstract record SimplePredicate : Condition
{
    /// <summary>Invert the predicate.</summary>
    [JsonPropertyName("negate")]
    public bool? Negate { get; init; }
}

public sealed record ProcessRunningPredicate : SimplePredicate
{
    [JsonPropertyName("processName")] public required string ProcessName { get; init; }
}

public sealed record WindowExistsPredicate : SimplePredicate
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
}

public sealed record PathExistsPredicate : SimplePredicate
{
    [JsonPropertyName("path")] public required string Path { get; init; }
}

public sealed record VariableEqualsPredicate : SimplePredicate
{
    [JsonPropertyName("variable")] public required string Variable { get; init; }
    [JsonPropertyName("value")] public required string Value { get; init; }
}

public sealed record VariableEmptyPredicate : SimplePredicate
{
    [JsonPropertyName("variable")] public required string Variable { get; init; }
}

public sealed record AllOfCondition : Condition
{
    [JsonPropertyName("conditions")]
    public required IReadOnlyList<SimplePredicate> Conditions { get; init; }
}

public sealed record AnyOfCondition : Condition
{
    [JsonPropertyName("conditions")]
    public required IReadOnlyList<SimplePredicate> Conditions { get; init; }
}

using System.Text.Json.Serialization;

namespace HotkeyAI.Core.Dsl;

/// <summary>
/// A single automation: a trigger plus an ordered list of actions.
/// </summary>
/// <remarks>
/// This mirrors the root object of <c>schema/hotkeyai-dsl-v1.schema.json</c>, which is the
/// authoritative contract. It carries only what a planner authors — runtime state (enabled
/// flag, version number, signature, execution history) is owned by the store and is
/// deliberately absent here.
/// </remarks>
public sealed record Automation
{
    /// <summary>Schema version this document targets. Always 1.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("trigger")]
    public required Trigger Trigger { get; init; }

    /// <summary>
    /// Variables the plan uses. Every variable written by an action's <c>into</c> must be
    /// declared here first; the policy validator enforces that, not the schema.
    /// </summary>
    [JsonPropertyName("variables")]
    public IReadOnlyList<VariableDeclaration> Variables { get; init; } = [];

    [JsonPropertyName("actions")]
    public IReadOnlyList<HotkeyAction> Actions { get; init; } = [];
}

/// <summary>What causes an automation to run. Only global hotkeys exist in v1.</summary>
public sealed record Trigger
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "hotkey";

    /// <summary>
    /// Modifiers followed by exactly one non-modifier key. Windows reserves some
    /// combinations and will refuse to register them; that failure surfaces at
    /// registration time, not here.
    /// </summary>
    [JsonPropertyName("keys")]
    public required IReadOnlyList<KeyName> Keys { get; init; }
}

/// <summary>Declaration of a variable, with the type the policy validator holds it to.</summary>
public sealed record VariableDeclaration
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required VariableType Type { get; init; }
}

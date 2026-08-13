using System.Text.Json.Serialization;
using HotkeyAI.Core.Json;

namespace HotkeyAI.Core.Dsl;

/// <summary>
/// Base for every action. The discriminator list below is the C# half of the conformance
/// contract: <c>SchemaConformanceTests</c> asserts it matches the schema's action
/// <c>oneOf</c> in both directions, so neither side can drift.
/// </summary>
/// <remarks>
/// Only <c>id</c> and <c>comment</c> live here because only those are universal. Actions
/// that cannot be verified (<c>wait</c>, <c>abort</c>) or that contain other actions
/// (<c>if</c>, <c>foreach</c>) deliberately do not inherit <c>timeoutMs</c>/<c>expect</c> â€”
/// the type system mirrors the schema rather than offering fields the schema rejects.
/// </remarks>
[JsonConverter(typeof(DiscriminatedJsonConverter<HotkeyAction>))]
[DslType(typeof(LaunchProcessAction), "launch_process")]
[DslType(typeof(TerminateProcessAction), "terminate_process")]
[DslType(typeof(WaitForProcessAction), "wait_for_process")]
[DslType(typeof(FocusWindowAction), "focus_window")]
[DslType(typeof(MinimizeWindowAction), "minimize_window")]
[DslType(typeof(MaximizeWindowAction), "maximize_window")]
[DslType(typeof(MoveWindowAction), "move_window")]
[DslType(typeof(CloseWindowAction), "close_window")]
[DslType(typeof(WaitForWindowAction), "wait_for_window")]
[DslType(typeof(SendKeysAction), "send_keys")]
[DslType(typeof(TypeTextAction), "type_text")]
[DslType(typeof(SendAppCommandAction), "send_appcommand")]
[DslType(typeof(ListDirectoriesAction), "list_directories")]
[DslType(typeof(ListFilesAction), "list_files")]
[DslType(typeof(PathExistsAction), "path_exists")]
[DslType(typeof(OpenPathAction), "open_path")]
[DslType(typeof(SetClipboardAction), "set_clipboard")]
[DslType(typeof(GetClipboardAction), "get_clipboard")]
[DslType(typeof(ShowPickerAction), "show_picker")]
[DslType(typeof(ShowInputAction), "show_input")]
[DslType(typeof(NotifyAction), "notify")]
[DslType(typeof(WaitAction), "wait")]
[DslType(typeof(AbortAction), "abort")]
[DslType(typeof(IfAction), "if")]
[DslType(typeof(ForEachAction), "foreach")]
public abstract record HotkeyAction
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
}

/// <summary>An action that can carry a timeout, an error policy, and a postcondition.</summary>
public abstract record VerifiableAction : HotkeyAction
{
    [JsonPropertyName("timeoutMs")] public int? TimeoutMs { get; init; }
    [JsonPropertyName("onError")] public OnErrorBehaviour? OnError { get; init; }
    [JsonPropertyName("expect")] public Postcondition? Expect { get; init; }
}

// ------------------------------- Process -------------------------------

/// <summary>Start an application. Exactly one of <see cref="App"/> or <see cref="Path"/>.</summary>
public sealed record LaunchProcessAction : VerifiableAction
{
    /// <summary>Logical app name resolved via the app registry. Preferred over Path.</summary>
    [JsonPropertyName("app")] public string? App { get; init; }

    /// <summary>Absolute path to an executable, constrained to allowed roots by policy.</summary>
    [JsonPropertyName("path")] public string? Path { get; init; }

    /// <summary>Separate arguments, never a command line. No shell, no quoting.</summary>
    [JsonPropertyName("argv")] public IReadOnlyList<string> Argv { get; init; } = [];

    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; init; }
}

public sealed record TerminateProcessAction : VerifiableAction
{
    [JsonPropertyName("processName")] public required string ProcessName { get; init; }
    [JsonPropertyName("force")] public bool? Force { get; init; }
}

public sealed record WaitForProcessAction : VerifiableAction
{
    [JsonPropertyName("processName")] public required string ProcessName { get; init; }
}

// ------------------------------- Window -------------------------------

public sealed record FocusWindowAction : VerifiableAction
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
}

public sealed record MinimizeWindowAction : VerifiableAction
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
}

public sealed record MaximizeWindowAction : VerifiableAction
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
}

public sealed record MoveWindowAction : VerifiableAction
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
    [JsonPropertyName("position")] public required WindowPosition Position { get; init; }

    /// <summary>"primary", "secondary", or a 1-based index as a string.</summary>
    [JsonPropertyName("monitor")] public string? Monitor { get; init; }
}

public sealed record CloseWindowAction : VerifiableAction
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
}

public sealed record WaitForWindowAction : VerifiableAction
{
    [JsonPropertyName("selector")] public required WindowSelector Selector { get; init; }
}

// ------------------------------- Input -------------------------------

public sealed record SendKeysAction : VerifiableAction
{
    [JsonPropertyName("keys")] public required IReadOnlyList<KeyName> Keys { get; init; }
    [JsonPropertyName("repeat")] public int? Repeat { get; init; }
}

public sealed record TypeTextAction : VerifiableAction
{
    [JsonPropertyName("text")] public required string Text { get; init; }
}

public sealed record SendAppCommandAction : VerifiableAction
{
    [JsonPropertyName("command")] public required AppCommand Command { get; init; }
}

// ------------------------------- Files -------------------------------

public sealed record ListDirectoriesAction : VerifiableAction
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("depth")] public int? Depth { get; init; }
    [JsonPropertyName("into")] public required string Into { get; init; }
}

public sealed record ListFilesAction : VerifiableAction
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("pattern")] public string? Pattern { get; init; }
    [JsonPropertyName("depth")] public int? Depth { get; init; }
    [JsonPropertyName("into")] public required string Into { get; init; }
}

public sealed record PathExistsAction : VerifiableAction
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("into")] public required string Into { get; init; }
}

public sealed record OpenPathAction : VerifiableAction
{
    [JsonPropertyName("path")] public required string Path { get; init; }
}

// ------------------------------- Clipboard -------------------------------

public sealed record SetClipboardAction : VerifiableAction
{
    [JsonPropertyName("text")] public required string Text { get; init; }
}

public sealed record GetClipboardAction : VerifiableAction
{
    [JsonPropertyName("into")] public required string Into { get; init; }
}

// ------------------------------- Prompts -------------------------------

public sealed record ShowPickerAction : VerifiableAction
{
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }
    [JsonPropertyName("into")] public required string Into { get; init; }
}

public sealed record ShowInputAction : VerifiableAction
{
    [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    [JsonPropertyName("defaultValue")] public string? DefaultValue { get; init; }
    [JsonPropertyName("into")] public required string Into { get; init; }
}

public sealed record NotifyAction : VerifiableAction
{
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("level")] public NotifyLevel? Level { get; init; }
}

// ------------------------------- Control -------------------------------

/// <summary>Fixed pause. Takes no postcondition â€” there is nothing to verify.</summary>
public sealed record WaitAction : HotkeyAction
{
    [JsonPropertyName("durationMs")] public required int DurationMs { get; init; }
    [JsonPropertyName("onError")] public OnErrorBehaviour? OnError { get; init; }
}

/// <summary>Stop the automation and report why.</summary>
public sealed record AbortAction : HotkeyAction
{
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>
/// Branch on a condition. Nesting depth is capped by the schema and re-checked by policy:
/// an <c>if</c> may contain another control-flow action, but that one may contain leaf
/// actions only.
/// </summary>
public sealed record IfAction : HotkeyAction
{
    [JsonPropertyName("condition")] public required Condition Condition { get; init; }
    [JsonPropertyName("then")] public IReadOnlyList<HotkeyAction> Then { get; init; } = [];
    [JsonPropertyName("else")] public IReadOnlyList<HotkeyAction> Else { get; init; } = [];
}

/// <summary>
/// Iterate a materialised list. Always bounded â€” by the list itself and by
/// <see cref="MaxIterations"/> â€” so it cannot run away.
/// </summary>
public sealed record ForEachAction : HotkeyAction
{
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("itemVariable")] public required string ItemVariable { get; init; }
    [JsonPropertyName("maxIterations")] public int? MaxIterations { get; init; }
    [JsonPropertyName("body")] public IReadOnlyList<HotkeyAction> Body { get; init; } = [];
}

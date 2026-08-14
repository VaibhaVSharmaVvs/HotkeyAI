using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Core.Policy;

/// <summary>
/// The second validation layer: everything the schema deliberately cannot express.
/// </summary>
/// <remarks>
/// <para>
/// The schema is kept inside the subset a constrained decoder can enforce, so it has no
/// numeric bounds, no knowledge of which paths this machine allows, and no way to reason about
/// dataflow between actions. Those checks live here. Splitting them is what lets the same
/// schema file be handed to a generating model unchanged in V2.
/// </para>
/// <para>
/// Runs only on a document that already passed the schema, so it may assume a well-formed
/// plan and check meaning rather than shape.
/// </para>
/// </remarks>
public static partial class PolicyValidator
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?:\.([A-Za-z0-9_]+))?\}")]
    private static partial Regex VariableReference { get; }

    /// <summary>Properties readable off a path variable, e.g. <c>${project.name}</c>.</summary>
    private static readonly HashSet<string> PathProperties =
        new(StringComparer.Ordinal) { "name", "fullPath", "parent", "extension" };

    /// <summary>Check a plan against the policy bounds.</summary>
    public static ValidationResult Validate(Automation automation, PolicyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(automation);
        options ??= PolicyOptions.Default;

        var errors = new List<ValidationError>();

        CheckTrigger(automation, errors);
        CheckDeclarations(automation, errors);

        var all = Walk(automation.Actions, "/actions", depth: 1).ToList();

        CheckSize(all, options, errors);
        CheckIds(all, errors);

        foreach (var (path, action, depth) in all)
        {
            CheckDepth(path, depth, options, errors);
            CheckBounds(path, action, options, errors);
            CheckLaunch(path, action, options, errors);
            CheckChord(path, action, errors);
        }

        CheckDataflow(automation, all, errors);

        return new ValidationResult(
            [.. errors.OrderBy(e => e.Path.Length).ThenBy(e => e.Path, StringComparer.Ordinal)]);
    }

    // ---------------------------------------------------------------------------------

    private static void CheckTrigger(Automation automation, List<ValidationError> errors)
    {
        var keys = automation.Trigger.Keys;
        var nonModifiers = keys.Count(k => !Keys.IsModifier(k));

        if (nonModifiers != 1)
        {
            errors.Add(Error(
                "/trigger/keys",
                $"A hotkey needs exactly one non-modifier key, found {nonModifiers}. "
                + "Example: [\"CTRL\", \"ALT\", \"P\"]."));
        }

        if (!keys.Any(Keys.IsModifier))
        {
            errors.Add(Error(
                "/trigger/keys",
                "A hotkey needs at least one modifier (CTRL, ALT, SHIFT or WIN). Registering a "
                + "bare key would swallow that key system-wide."));
        }

        if (keys.Distinct().Count() != keys.Count)
        {
            errors.Add(Error("/trigger/keys", "The chord repeats a key."));
        }
    }

    private static void CheckDeclarations(Automation automation, List<ValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < automation.Variables.Count; i++)
        {
            var declaration = automation.Variables[i];
            if (!seen.Add(declaration.Name))
            {
                errors.Add(Error(
                    $"/variables/{i}",
                    $"Variable \"{declaration.Name}\" is declared more than once."));
            }
        }
    }

    private static void CheckSize(
        List<(string Path, HotkeyAction Action, int Depth)> all,
        PolicyOptions options,
        List<ValidationError> errors)
    {
        if (all.Count > options.MaxActions)
        {
            errors.Add(Error(
                "/actions",
                $"This plan has {all.Count} actions; the cap is {options.MaxActions}. The cap "
                + "exists so a runaway automation cannot hold the desktop hostage."));
        }
    }

    private static void CheckIds(
        List<(string Path, HotkeyAction Action, int Depth)> all, List<ValidationError> errors)
    {
        var byId = all
            .Where(a => !string.IsNullOrEmpty(a.Action.Id))
            .GroupBy(a => a.Action.Id!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var duplicate in byId)
        {
            foreach (var (path, _, _) in duplicate.Skip(1))
            {
                errors.Add(Error(
                    path,
                    $"Action id \"{duplicate.Key}\" is used more than once. Ids appear in "
                    + "execution logs and failure reports, so they must be unique."));
            }
        }
    }

    private static void CheckDepth(
        string path, int depth, PolicyOptions options, List<ValidationError> errors)
    {
        if (depth > options.MaxNestingDepth)
        {
            errors.Add(Error(
                path,
                $"Nested {depth} levels deep; the limit is {options.MaxNestingDepth}. Control "
                + "flow may nest one level, and the inner one holds leaf actions only."));
        }
    }

    private static void CheckBounds(
        string path, HotkeyAction action, PolicyOptions options, List<ValidationError> errors)
    {
        if (action is VerifiableAction verifiable)
        {
            Bound(path + "/timeoutMs", verifiable.TimeoutMs, options.Timeout, errors);

            if (verifiable.Expect is { WithinMs: { } within })
            {
                Bound(path + "/expect/withinMs", within, options.Within, errors);
            }
        }

        switch (action)
        {
            case WaitAction wait:
                Bound(path + "/durationMs", wait.DurationMs, options.WaitDuration, errors);
                break;

            case ListDirectoriesAction list:
                Bound(path + "/depth", list.Depth, options.ListDepth, errors);
                break;

            case ListFilesAction list:
                Bound(path + "/depth", list.Depth, options.ListDepth, errors);
                break;

            case ForEachAction loop:
                Bound(path + "/maxIterations", loop.MaxIterations, options.Iterations, errors);
                break;

            case SendKeysAction keys:
                Bound(path + "/repeat", keys.Repeat, options.KeyRepeat, errors);
                break;
        }
    }

    private static void Bound(
        string path, int? value, Range<int> range, List<ValidationError> errors)
    {
        if (value is { } actual && !range.Contains(actual))
        {
            errors.Add(Error(
                path,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{actual} is outside the permitted range ({range}).")));
        }
    }

    private static void CheckLaunch(
        string path, HotkeyAction action, PolicyOptions options, List<ValidationError> errors)
    {
        if (action is not LaunchProcessAction launch)
        {
            return;
        }

        if (launch.App is { } app && !options.KnownApps.Contains(app))
        {
            errors.Add(Error(
                path + "/app",
                $"\"{app}\" is not a known application. Known: "
                + string.Join(", ", options.KnownApps.Order(StringComparer.Ordinal))
                + ". Use an absolute path under an allowed root if it is not listed."));
        }

        if (launch.Path is not { } literal)
        {
            return;
        }

        // A path built from a variable cannot be checked statically. Validate the part that is
        // literal and rely on the executor to re-check the resolved value — a static check that
        // interpolation can slip past is a false sense of security, not a boundary.
        if (literal.Contains("${", StringComparison.Ordinal))
        {
            errors.Add(Error(
                path + "/path",
                "This path is built from a variable, so it cannot be checked before the plan "
                + "runs. Prefer a logical app name; if a path is genuinely needed, the engine "
                + "will re-check it against the allowed roots at execution time and abort."));
            return;
        }

        if (!WindowsPath.IsAbsolute(literal))
        {
            errors.Add(Error(path + "/path", $"\"{literal}\" is not an absolute Windows path."));
            return;
        }

        if (options.AllowedRoots.Count == 0)
        {
            errors.Add(Error(
                path + "/path",
                "No allowed roots are configured, so launching by path is refused. Use a "
                + "logical app name, or configure an allowed root."));
            return;
        }

        if (!options.AllowedRoots.Any(root => WindowsPath.IsUnder(literal, root)))
        {
            errors.Add(Error(
                path + "/path",
                $"\"{literal}\" is not under an allowed root ("
                + string.Join(", ", options.AllowedRoots) + ")."));
        }
    }

    private static void CheckChord(string path, HotkeyAction action, List<ValidationError> errors)
    {
        if (action is not SendKeysAction send)
        {
            return;
        }

        var nonModifiers = send.Keys.Count(k => !Keys.IsModifier(k));
        if (nonModifiers != 1)
        {
            errors.Add(Error(
                path + "/keys",
                $"A chord needs exactly one non-modifier key, found {nonModifiers}."));
        }
    }

    // --------------------------------- dataflow ---------------------------------

    /// <summary>
    /// Check that variables are declared, written with the right type, and not read before
    /// anything has written them.
    /// </summary>
    /// <remarks>
    /// Flow-insensitive across <c>if</c> branches on purpose: a variable assigned only inside a
    /// branch counts as assigned afterwards. Being strict there would reject reasonable plans
    /// for a problem the runtime handles, whereas the mistake actually worth catching — reading
    /// a variable nothing ever writes, usually a typo — is caught either way. A
    /// <c>foreach</c> item variable is the exception: it exists only inside the loop, so
    /// reading it afterwards is always wrong.
    /// </remarks>
    private static void CheckDataflow(
        Automation automation,
        List<(string Path, HotkeyAction Action, int Depth)> all,
        List<ValidationError> errors)
    {
        // Built defensively rather than with ToDictionary: a duplicate declaration is a plan
        // this layer is meant to *reject*, and throwing while assembling the map would crash
        // the validator on exactly the input it exists to catch. First declaration wins; the
        // duplicate itself is already reported by CheckDeclarations.
        var declared = new Dictionary<string, VariableType>(StringComparer.Ordinal);
        foreach (var variable in automation.Variables)
        {
            declared.TryAdd(variable.Name, variable.Type);
        }

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var loopScoped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, action, _) in all)
        {
            foreach (var (name, property, field) in References(action))
            {
                if (!declared.TryGetValue(name, out var type))
                {
                    errors.Add(Error(
                        $"{path}/{field}",
                        $"${{{name}}} is not declared. Add it to \"variables\" with its type."));
                    continue;
                }

                if (!assigned.Contains(name))
                {
                    errors.Add(Error(
                        $"{path}/{field}",
                        $"${{{name}}} is read before anything assigns it."));
                }

                if (property is not null)
                {
                    CheckProperty(path, field, name, property, type, errors);
                }
            }

            // Writes happen after reads within the same action: an action never reads the
            // variable it is about to write.
            RecordWrites(path, action, declared, assigned, loopScoped, errors);
        }
    }

    private static void CheckProperty(
        string path,
        string field,
        string name,
        string property,
        VariableType type,
        List<ValidationError> errors)
    {
        if (!PathProperties.Contains(property))
        {
            errors.Add(Error(
                $"{path}/{field}",
                $"\"{property}\" is not a readable property. Available on a path: "
                + string.Join(", ", PathProperties.Order(StringComparer.Ordinal)) + "."));
            return;
        }

        if (type is not (VariableType.Path or VariableType.PathList))
        {
            errors.Add(Error(
                $"{path}/{field}",
                $"${{{name}.{property}}} reads a path property, but \"{name}\" is declared "
                + $"{WireName.Of(type)}."));
        }
    }

    private static void RecordWrites(
        string path,
        HotkeyAction action,
        Dictionary<string, VariableType> declared,
        HashSet<string> assigned,
        HashSet<string> loopScoped,
        List<ValidationError> errors)
    {
        switch (action)
        {
            case ListDirectoriesAction a:
                Write(path, "into", a.Into, VariableType.PathList, declared, assigned, errors);
                break;

            case ListFilesAction a:
                Write(path, "into", a.Into, VariableType.PathList, declared, assigned, errors);
                break;

            case PathExistsAction a:
                Write(path, "into", a.Into, VariableType.Boolean, declared, assigned, errors);
                break;

            case GetClipboardAction a:
                Write(path, "into", a.Into, VariableType.Text, declared, assigned, errors);
                break;

            case ShowInputAction a:
                Write(path, "into", a.Into, VariableType.Text, declared, assigned, errors);
                break;

            case ShowPickerAction a:
                WriteFromList(path, a.Source, "source", a.Into, "into", declared, assigned, errors);
                break;

            case ForEachAction a:
                WriteFromList(
                    path, a.Source, "source", a.ItemVariable, "itemVariable",
                    declared, assigned, errors);

                // Scoped to the body. Reading it after the loop is a genuine mistake.
                loopScoped.Add(a.ItemVariable);
                break;
        }
    }

    private static void Write(
        string path,
        string field,
        string name,
        VariableType expected,
        Dictionary<string, VariableType> declared,
        HashSet<string> assigned,
        List<ValidationError> errors)
    {
        if (!declared.TryGetValue(name, out var actual))
        {
            errors.Add(Error(
                $"{path}/{field}",
                $"\"{name}\" is not declared. Add it to \"variables\" as "
                + $"{WireName.Of(expected)}."));
            return;
        }

        if (actual != expected)
        {
            errors.Add(Error(
                $"{path}/{field}",
                $"This writes a {WireName.Of(expected)}, but \"{name}\" is "
                + $"declared {WireName.Of(actual)}."));
            return;
        }

        assigned.Add(name);
    }

    /// <summary>Write an element drawn from a list variable, matching the element type.</summary>
    private static void WriteFromList(
        string path,
        string source,
        string sourceField,
        string target,
        string targetField,
        Dictionary<string, VariableType> declared,
        HashSet<string> assigned,
        List<ValidationError> errors)
    {
        if (!declared.TryGetValue(source, out var sourceType))
        {
            errors.Add(Error(
                $"{path}/{sourceField}",
                $"\"{source}\" is not declared."));
            return;
        }

        var element = sourceType switch
        {
            VariableType.PathList => (VariableType?)VariableType.Path,
            VariableType.TextList => VariableType.Text,
            _ => null,
        };

        if (element is null)
        {
            errors.Add(Error(
                $"{path}/{sourceField}",
                $"\"{source}\" is declared {WireName.Of(sourceType)}, but this "
                + "needs a pathList or a textList."));
            return;
        }

        Write(path, targetField, target, element.Value, declared, assigned, errors);
    }

    // --------------------------------- plumbing ---------------------------------

    /// <summary>
    /// Every <c>${...}</c> reference in an action's own string fields.
    /// </summary>
    /// <remarks>
    /// Found by reflection rather than a per-action switch. A switch would need a case for
    /// every primitive, and a missed case is a silent false negative — an unchecked variable
    /// reference — whereas reflection picks up new primitives automatically. Fields naming a
    /// variable rather than interpolating one (<c>into</c>, <c>source</c>) are excluded; they
    /// are handled as writes.
    /// </remarks>
    private static IEnumerable<(string Name, string? Property, string Field)> References(
        HotkeyAction action)
    {
        var naming = new[] { "Into", "Source", "ItemVariable", "Variable" };

        foreach (var property in action.GetType().GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (naming.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var field = JsonName(property);

            foreach (var text in Strings(property.GetValue(action)))
            {
                foreach (Match match in VariableReference.Matches(text))
                {
                    yield return (
                        match.Groups[1].Value,
                        match.Groups[2].Success ? match.Groups[2].Value : null,
                        field);
                }
            }
        }
    }

    private static IEnumerable<string> Strings(object? value) => value switch
    {
        string text => [text],
        IEnumerable<string> many => many,
        WindowSelector selector => new[]
            {
                selector.TitleContains, selector.TitleRegex,
                selector.ProcessName, selector.ClassName,
            }.OfType<string>(),
        _ => [],
    };

    private static string JsonName(PropertyInfo property) =>
        property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
            ?.Name ?? property.Name;

    /// <summary>Every action, depth-first, with its pointer and nesting level.</summary>
    private static IEnumerable<(string Path, HotkeyAction Action, int Depth)> Walk(
        IReadOnlyList<HotkeyAction> actions, string prefix, int depth)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var path = $"{prefix}/{i}";

            yield return (path, action, depth);

            switch (action)
            {
                case IfAction branch:
                    foreach (var nested in Walk(branch.Then, path + "/then", depth + 1))
                    {
                        yield return nested;
                    }

                    foreach (var nested in Walk(branch.Else, path + "/else", depth + 1))
                    {
                        yield return nested;
                    }

                    break;

                case ForEachAction loop:
                    foreach (var nested in Walk(loop.Body, path + "/body", depth + 1))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static ValidationError Error(string path, string message) =>
        new(ValidationLayer.Policy, path, message);
}

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
            CheckSelectors(path, action, options, errors);
            CheckOpen(path, action, errors);
            CheckPaths(path, action, options, errors);
        }

        CheckDataflow(automation, all, errors);

        return new ValidationResult(
            [.. errors.OrderBy(e => e.Path.Length).ThenBy(e => e.Path, StringComparer.Ordinal)]);
    }

    // ---------------------------------------------------------------------------------

    private static void CheckTrigger(Automation automation, List<ValidationError> errors)
    {
        // One rule, in HotkeyChord, shared with the dashboard's capture control. A second copy
        // here would eventually tell the user a chord is fine that this validator then refuses.
        foreach (var problem in HotkeyChord.Problems(automation.Trigger.Keys))
        {
            errors.Add(Error("/trigger/keys", problem));
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
        if (all.Count == 0)
        {
            // An empty plan used to validate, be approvable, and bind a global chord that did
            // nothing — and a chord is process-wide and first-come-first-served, so it also took
            // that combination away from whatever else on the machine wanted it. Refused rather
            // than warned because there is no ValidationResult severity below "error", and a plan
            // with no actions is a mistake in every case: nobody writes one on purpose, and the
            // honest reading of one is an edit that went wrong.
            errors.Add(Error(
                "/actions",
                "This plan has no actions, so its hotkey would do nothing while still claiming "
                + "the key combination system-wide. Add at least one action, or delete the plan."));
        }

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

    /// <summary>
    /// Check every literal path in an action, not just the one on <c>launch_process</c>.
    /// </summary>
    /// <remarks>
    /// This used to cover <c>launch_process.path</c> alone. Out-of-root literals on
    /// <c>open_path</c>, <c>list_files</c>, <c>list_directories</c>, <c>path_exists</c>,
    /// <c>workingDirectory</c> and <c>expect.path_exists</c> validated clean and failed only at run
    /// time — a plan the user could approve and that could never work. The runtime guard held, so
    /// this is honesty rather than a hole, and the honesty matters most on the approval screen:
    /// someone reading a preview is being asked whether to trust a plan, and "this cannot run" is
    /// something they should learn then rather than on the keypress.
    /// <para>
    /// Only when roots are configured. With none, the run-time guard refuses every path anyway, and
    /// a validator that rejected every literal under <see cref="PolicyOptions.Default"/> would be
    /// useless for authoring — the question "is this under an allowed root" has no answer worth
    /// giving when there are no roots to be under.
    /// </para>
    /// <para>
    /// <c>launch_process.path</c> keeps its own stricter treatment in <see cref="CheckLaunch"/>: it
    /// refuses an interpolated path outright, because launching is the one operation where a value
    /// that cannot be checked before the plan runs is worth refusing rather than re-checking.
    /// </para>
    /// </remarks>
    private static void CheckPaths(
        string path, HotkeyAction action, PolicyOptions options, List<ValidationError> errors)
    {
        foreach (var (pointer, literal) in Paths(action, path))
        {
            // launch_process.path is CheckLaunch's, and reporting it twice in different words would
            // be worse than not reporting it here at all.
            if (action is LaunchProcessAction && pointer == path + "/path")
            {
                continue;
            }

            // Interpolated: nothing to check statically, and the executor re-checks the resolved
            // value against the same roots before touching it.
            if (literal.Length == 0 || literal.Contains("${", StringComparison.Ordinal))
            {
                continue;
            }

            if (!WindowsPath.IsAbsolute(literal))
            {
                errors.Add(Error(
                    pointer,
                    $"\"{literal}\" is not an absolute Windows path. A relative path has nothing "
                    + "to be relative to — an automation runs from wherever the agent happens to "
                    + "be — so it can never resolve."));
                continue;
            }

            if (options.AllowedRoots.Count > 0
                && !options.AllowedRoots.Any(root => WindowsPath.IsUnder(literal, root)))
            {
                errors.Add(Error(
                    pointer,
                    $"\"{literal}\" is not under an allowed root ("
                    + string.Join(", ", options.AllowedRoots)
                    + "). The engine would refuse this at run time, so the plan could be approved "
                    + "and still never work."));
            }
        }
    }

    /// <summary>Every filesystem path an action carries, with the pointer that reaches it.</summary>
    /// <remarks>
    /// Found by JSON name — <c>path</c> and <c>workingDirectory</c> — rather than by naming the
    /// records, for the reason the rest of this file gives: a primitive added later is covered
    /// without anyone remembering to come back here. There is no field called <c>path</c> in the
    /// DSL that is not a filesystem path, and a postcondition or predicate carrying one is reached
    /// through the same walk.
    /// </remarks>
    private static IEnumerable<(string Path, string Literal)> Paths(object value, string path)
    {
        if (value is IEnumerable<Condition> conditions)
        {
            var index = 0;
            foreach (var condition in conditions)
            {
                foreach (var found in Paths(condition, $"{path}/{index++}"))
                {
                    yield return found;
                }
            }

            yield break;
        }

        if (value is not (HotkeyAction or Postcondition or Condition))
        {
            yield break;
        }

        foreach (var property in value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0))
        {
            if (SafeRead(property, value) is not { } inner || inner is IEnumerable<HotkeyAction>)
            {
                continue;
            }

            var name = JsonName(property);

            if (inner is string literal)
            {
                if (name is "path" or "workingDirectory")
                {
                    yield return ($"{path}/{name}", literal);
                }

                continue;
            }

            foreach (var found in Paths(inner, $"{path}/{name}"))
            {
                yield return found;
            }
        }
    }

    /// <summary>
    /// Refuse an <c>open_path</c> whose literal path is something Windows executes.
    /// </summary>
    /// <remarks>
    /// The executor refuses it too, and has to — a path built from a variable is only known at run
    /// time, which is the amplifying shape that matters: <c>list_files</c> over a folder anyone can
    /// write to, then <c>foreach</c> → <c>open_path</c>. This half is about the other failure mode:
    /// a plan naming an <c>.exe</c> outright used to validate clean and be approvable, so the
    /// user's yes was given to something that could never have been allowed to run.
    /// </remarks>
    private static void CheckOpen(string path, HotkeyAction action, List<ValidationError> errors)
    {
        if (action is not OpenPathAction open
            || open.Path.Contains("${", StringComparison.Ordinal))
        {
            return;
        }

        if (!ShellOpen.IsAllowed(open.Path, out var reason))
        {
            errors.Add(Error(path + "/path", reason));
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

    /// <summary>
    /// Refuse a <c>titleRegex</c> the engine cannot run safely.
    /// </summary>
    /// <remarks>
    /// The engine matches titles on .NET's non-backtracking
    /// engine, which is linear in the input and therefore immune to catastrophic backtracking —
    /// but it refuses lookaround, backreferences and atomic groups, and it refuses them by throwing
    /// when the pattern is constructed. Discovering that on a keypress would mean an automation the
    /// user approved failing at the moment they needed it, so the same construction is attempted
    /// here, where the answer becomes a validation error with a pointer.
    /// <para>
    /// The length cap is separate and cruder: a pattern is a machine-authored string in V2, and a
    /// very long one is a sign of something other than a window title being matched.
    /// </para>
    /// </remarks>
    private static void CheckSelectors(
        string path, HotkeyAction action, PolicyOptions options, List<ValidationError> errors)
    {
        foreach (var (pointer, selector) in Selectors(action, path))
        {
            if (selector.TitleRegex is not { } pattern)
            {
                continue;
            }

            if (pattern.Length > options.MaxTitleRegexLength)
            {
                errors.Add(Error(
                    pointer + "/titleRegex",
                    $"The pattern is {pattern.Length} characters, over the limit of "
                    + $"{options.MaxTitleRegexLength}. A window-title pattern this long is "
                    + "matching something other than a window title."));
                continue;
            }

            try
            {
                // Construction is the whole check: it parses the pattern and decides whether the
                // linear-time engine will accept it. Nothing is matched here.
                _ = new Regex(pattern, RegexOptions.NonBacktracking);
            }
            catch (ArgumentException invalid)
            {
                errors.Add(Error(
                    pointer + "/titleRegex",
                    $"\"{pattern}\" is not a valid regular expression: {invalid.Message}"));
            }
            catch (NotSupportedException unsupported)
            {
                errors.Add(Error(
                    pointer + "/titleRegex",
                    $"\"{pattern}\" uses a construct window matching does not allow. Titles are "
                    + "matched by an engine that runs in time linear in the title's length, so a "
                    + "pattern can never hang the desktop — the price is that lookaround, "
                    + "backreferences and atomic groups are unavailable. Rewrite the pattern "
                    + $"without them, or use titleContains. ({unsupported.Message})"));
            }
        }
    }

    /// <summary>Every window selector an action carries, with the pointer that reaches it.</summary>
    /// <remarks>
    /// Reflective for the same reason <see cref="Strings"/> is: a selector can sit directly on an
    /// action, inside its <c>expect</c>, or inside a predicate — including one nested in an
    /// <c>all_of</c> — and naming those places one by one is how a later primitive gets missed.
    /// </remarks>
    private static IEnumerable<(string Path, WindowSelector Selector)> Selectors(
        object value, string path)
    {
        if (value is WindowSelector selector)
        {
            yield return (path, selector);
            yield break;
        }

        if (value is IEnumerable<Condition> conditions)
        {
            var index = 0;
            foreach (var condition in conditions)
            {
                foreach (var found in Selectors(condition, $"{path}/{index++}"))
                {
                    yield return found;
                }
            }

            yield break;
        }

        if (value is not (HotkeyAction or Postcondition or Condition))
        {
            yield break;
        }

        foreach (var property in value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0))
        {
            // Nested action lists belong to the outer walk, which visits them with their own
            // pointers — descending into them here would report the same selector twice.
            if (SafeRead(property, value) is not { } inner
                || inner is IEnumerable<HotkeyAction>)
            {
                continue;
            }

            foreach (var found in Selectors(inner, $"{path}/{JsonName(property)}"))
            {
                yield return found;
            }
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
        // Maps a foreach item variable to the pointer of the loop that owns it, so a read can be
        // told apart from a read *after* the loop. This was a HashSet that nothing ever read, so
        // the rule the doc comment above states plainly — and which the executor enforces at run
        // time by clearing the variable — was never actually checked. A plan reading a loop
        // variable afterwards validated clean and then silently interpolated an empty string.
        var loopScoped = new Dictionary<string, string>(StringComparer.Ordinal);

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
                else if (Escaped(loopScoped, name, path))
                {
                    errors.Add(Error(
                        $"{path}/{field}",
                        $"${{{name}}} is a foreach item variable, so it only exists inside its "
                        + "loop. The engine clears it when the loop ends, so reading it here "
                        + "would interpolate an empty string."));
                }

                if (property is not null)
                {
                    CheckProperty(path, field, name, property, type, errors);
                }
            }

            // Writes happen after reads within the same action: an action never reads the
            // variable it is about to write.
            RecordWrites(path, action, declared, assigned, loopScoped, errors);

            // The postcondition, though, runs after the action succeeded — so it may read what the
            // action just wrote, and is checked on this side of the write.
            foreach (var (name, property, field) in ExpectReferences(action))
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
                else if (Escaped(loopScoped, name, path))
                {
                    errors.Add(Error(
                        $"{path}/{field}",
                        $"${{{name}}} is a foreach item variable, so it only exists inside its "
                        + "loop. The engine clears it when the loop ends, so reading it here "
                        + "would interpolate an empty string."));
                }

                if (property is not null)
                {
                    CheckProperty(path, field, name, property, type, errors);
                }
            }
        }
    }

    /// <summary>
    /// Whether this read of a loop item variable happens outside the loop that owns it.
    /// </summary>
    /// <remarks>
    /// Decided by pointer prefix, which works because the walk is depth-first: every action inside
    /// a loop has the loop's pointer as a prefix, and the first action that does not is the first
    /// one after it. The loop action itself is excluded — it reads <c>source</c>, not the item.
    /// </remarks>
    private static bool Escaped(
        Dictionary<string, string> loopScoped, string name, string path) =>
        loopScoped.TryGetValue(name, out var owner)
        && !path.StartsWith(owner + "/", StringComparison.Ordinal)
        && !string.Equals(path, owner, StringComparison.Ordinal);

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
        Dictionary<string, string> loopScoped,
        List<ValidationError> errors)
    {
        // Anything other than a foreach assigning the name means it is no longer only a loop
        // item, so reading it after the loop becomes legitimate again. Without this, reusing a
        // name for both purposes would be reported forever after the first loop.
        switch (action)
        {
            case ListDirectoriesAction a:
                Write(path, "into", a.Into, VariableType.PathList, declared, assigned, errors);
                loopScoped.Remove(a.Into);
                break;

            case ListFilesAction a:
                Write(path, "into", a.Into, VariableType.PathList, declared, assigned, errors);
                loopScoped.Remove(a.Into);
                break;

            case PathExistsAction a:
                Write(path, "into", a.Into, VariableType.Boolean, declared, assigned, errors);
                loopScoped.Remove(a.Into);
                break;

            case GetClipboardAction a:
                Write(path, "into", a.Into, VariableType.Text, declared, assigned, errors);
                loopScoped.Remove(a.Into);
                break;

            case ShowInputAction a:
                Write(path, "into", a.Into, VariableType.Text, declared, assigned, errors);
                loopScoped.Remove(a.Into);
                break;

            case ShowPickerAction a:
                WriteFromList(path, a.Source, "source", a.Into, "into", declared, assigned, errors);
                loopScoped.Remove(a.Into);
                break;

            case ForEachAction a:
                WriteFromList(
                    path, a.Source, "source", a.ItemVariable, "itemVariable",
                    declared, assigned, errors);

                // Scoped to the body. Reading it after the loop is a genuine mistake.
                loopScoped[a.ItemVariable] = path;
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
    /// <summary>Variables an action reads to do its work, before it writes anything.</summary>
    private static IEnumerable<(string Name, string? Property, string Field)> References(
        HotkeyAction action) => References(action, expectations: false);

    /// <summary>
    /// Variables an action's postcondition reads, which happens after its own write.
    /// </summary>
    /// <remarks>
    /// Split out because sequence matters and the two halves sit on opposite sides of the write. An
    /// action never reads the variable it is about to write — but its <c>expect</c> runs once the
    /// action has succeeded, so <c>get_clipboard</c> into <c>got</c> with <c>expect:
    /// clipboard_matches contains ${got}</c> is not only legal, it is the natural way to verify a
    /// clipboard write. Checking both halves before the write reported that as reading a variable
    /// before anything assigned it.
    /// <para>
    /// Found once expectations became visible to the dataflow walk at all: until then this
    /// ordering question appeared with them.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Name, string? Property, string Field)> ExpectReferences(
        HotkeyAction action) => References(action, expectations: true);

    private static IEnumerable<(string Name, string? Property, string Field)> References(
        HotkeyAction action, bool expectations)
    {
        var naming = new[] { "Into", "Source", "ItemVariable", "Variable" };

        foreach (var property in action.GetType().GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (naming.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (string.Equals(property.Name, nameof(VerifiableAction.Expect), StringComparison.Ordinal)
                != expectations)
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

    /// <summary>
    /// Every string an action carries, however deeply nested.
    /// </summary>
    /// <remarks>
    /// This once handled <c>string</c>,
    /// <c>IEnumerable&lt;string&gt;</c> and <see cref="WindowSelector"/>, and everything else fell
    /// through to nothing — so a <c>${...}</c> inside an <c>expect</c> or a predicate was invisible
    /// to the declaration and assignment checks, and a plan reading a variable nothing ever wrote
    /// validated clean. That is what let a vacuous postcondition reach run time from a valid plan:
    /// the executor interpolated the unwritten variable to an empty string, and a check that
    /// compared against nothing reported "verified".
    /// <para>
    /// It is a false negative in the layer whose own comment says reflection was chosen over a
    /// switch precisely because "a missed case is a silent false negative". The fix keeps that
    /// spirit — a reflective walk over any nested DSL record — so a primitive added later is
    /// covered without anyone remembering to come back here.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Strings(object? value) => value switch
    {
        null => [],
        string text => [text],
        IEnumerable<string> many => many,

        // The DSL's own nested record types: postconditions, conditions, predicates, selectors.
        // Walked by reflection rather than named one by one, so this does not have to be revisited
        // every time the schema grows.
        Postcondition or Condition or WindowSelector => Nested(value),

        // A composite condition holds a list of more conditions.
        IEnumerable<Condition> conditions => conditions.SelectMany(Strings),

        _ => [],
    };

    /// <summary>Reflect over a DSL record's own properties and gather every string inside.</summary>
    private static IEnumerable<string> Nested(object value) =>
        value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .SelectMany(p => Strings(SafeRead(p, value)));

    private static object? SafeRead(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
#pragma warning disable CA1031 // A property that throws must not fail validation of the whole plan.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

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

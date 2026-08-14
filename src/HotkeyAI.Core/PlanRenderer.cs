using System.Globalization;
using System.Text;
using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Core;

/// <summary>
/// Renders a plan as readable prose.
/// </summary>
/// <remarks>
/// <para>
/// This lives in Core because two surfaces must show the user the same thing: the UI's plan
/// preview before anything runs, and <c>HotkeyAI.Cli explain</c>. If they were rendered
/// separately they would drift, and the preview a user approves would stop matching the plan
/// that executes.
/// </para>
/// <para>
/// It is deliberately explicit about verification. An action with no postcondition is marked
/// unverified rather than quietly presented as if it succeeds — the engine genuinely cannot
/// tell whether it worked, and the preview must not imply otherwise.
/// </para>
/// </remarks>
public static class PlanRenderer
{
    private const string Verified = "verified";
    private const string Unverified = "unverified";

    /// <summary>Render a full plan.</summary>
    public static string Explain(Automation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);

        var text = new StringBuilder();
        text.AppendLine(automation.Name);

        if (!string.IsNullOrWhiteSpace(automation.Description))
        {
            text.AppendLine(automation.Description);
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Trigger    {DescribeTrigger(automation.Trigger)}");

        if (automation.Variables.Count > 0)
        {
            var declared = automation.Variables.Select(v => $"{v.Name}: {Wire(v.Type)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Variables  {string.Join(", ", declared)}");
        }

        text.AppendLine();
        text.AppendLine("Steps");
        RenderSteps(automation.Actions, text, depth: 0, prefix: "");

        var all = Flatten(automation.Actions).ToList();
        var verifiable = all.OfType<VerifiableAction>().ToList();
        var verified = verifiable.Count(a => a.Expect is not null);

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{verified} of {all.Count} actions carry a postcondition the engine can check.");

        if (verified < verifiable.Count)
        {
            text.AppendLine(
                "The rest are reported as unverified: the engine will run them but cannot "
                + "confirm they had any effect.");
        }

        return text.ToString();
    }

    private static void RenderSteps(
        IReadOnlyList<HotkeyAction> actions, StringBuilder text, int depth, string prefix)
    {
        var indent = new string(' ', 2 + (depth * 4));

        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var number = prefix.Length == 0
                ? (i + 1).ToString(CultureInfo.InvariantCulture)
                : $"{prefix}.{i + 1}";

            var id = string.IsNullOrEmpty(action.Id) ? "" : $" [{action.Id}]";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{indent}{number}.{id} {DescribeAction(action)}");

            if (!string.IsNullOrWhiteSpace(action.Comment))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{indent}    note: {action.Comment}");
            }

            if (action is VerifiableAction verifiable)
            {
                var check = verifiable.Expect is null
                    ? $"({Unverified})"
                    : $"({Verified}) {Describe(verifiable.Expect)}";

                text.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {check}");

                if (verifiable.OnError == OnErrorBehaviour.Continue)
                {
                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"{indent}    on failure: continue to the next action");
                }
            }

            switch (action)
            {
                case IfAction branch:
                    text.AppendLine(CultureInfo.InvariantCulture, $"{indent}    then:");
                    RenderSteps(branch.Then, text, depth + 1, number);

                    if (branch.Else.Count > 0)
                    {
                        text.AppendLine(CultureInfo.InvariantCulture, $"{indent}    otherwise:");
                        RenderSteps(branch.Else, text, depth + 1, number + "e");
                    }

                    break;

                case ForEachAction loop:
                    RenderSteps(loop.Body, text, depth + 1, number);
                    break;
            }
        }
    }

    /// <summary>One-line description of a single action.</summary>
    /// <exception cref="NotSupportedException">
    /// A DSL action exists with no rendering. This is a programming error, not a data error —
    /// the schema conformance test guarantees the type set, so a gap here means a primitive
    /// was added without teaching the renderer about it.
    /// </exception>
    public static string DescribeAction(HotkeyAction action) => action switch
    {
        LaunchProcessAction a =>
            $"Launch {a.App ?? a.Path}"
            + (a.Argv.Count > 0 ? $" with {string.Join(" ", a.Argv)}" : ""),

        TerminateProcessAction a =>
            $"{(a.Force == true ? "Force-kill" : "Terminate")} process \"{a.ProcessName}\"",

        WaitForProcessAction a => $"Wait until process \"{a.ProcessName}\" is running",

        FocusWindowAction a => $"Focus {Describe(a.Selector)}",
        MinimizeWindowAction a => $"Minimize {Describe(a.Selector)}",
        MaximizeWindowAction a => $"Maximize {Describe(a.Selector)}",
        CloseWindowAction a => $"Ask {Describe(a.Selector)} to close",
        WaitForWindowAction a => $"Wait for {Describe(a.Selector)} to appear",

        MoveWindowAction a =>
            $"Move {Describe(a.Selector)} to {Wire(a.Position).Replace('_', ' ')}"
            + (a.Monitor is null ? "" : $" on the {a.Monitor} monitor"),

        SendKeysAction a =>
            $"Press {Chord(a.Keys)}"
            + (a.Repeat is > 1 ? $", {a.Repeat} times" : "")
            + " in the focused window",

        TypeTextAction a => $"Type \"{Ellipsis(a.Text)}\" into the focused window",

        SendAppCommandAction a =>
            $"Send the system {Wire(a.Command).Replace('_', ' ')} command",

        ListDirectoriesAction a =>
            $"List folders in {a.Path} (depth {a.Depth ?? 1}) into ${{{a.Into}}}",

        ListFilesAction a =>
            $"List files matching {a.Pattern ?? "*"} in {a.Path} "
            + $"(depth {a.Depth ?? 1}) into ${{{a.Into}}}",

        PathExistsAction a => $"Check whether {a.Path} exists, into ${{{a.Into}}}",

        OpenPathAction a => $"Open {a.Path} with its default application",

        SetClipboardAction a => $"Copy \"{Ellipsis(a.Text)}\" to the clipboard",
        GetClipboardAction a => $"Read the clipboard into ${{{a.Into}}}",

        ShowPickerAction a =>
            $"Ask the user to pick from ${{{a.Source}}} into ${{{a.Into}}}"
            + (a.Prompt is null ? "" : $" — \"{a.Prompt}\""),

        ShowInputAction a => $"Prompt the user: \"{a.Prompt}\" into ${{{a.Into}}}",

        NotifyAction a => $"Show a {Wire(a.Level ?? NotifyLevel.Info)} toast: \"{a.Message}\"",

        WaitAction a => $"Wait {a.DurationMs} ms",

        AbortAction a =>
            "Stop the automation" + (a.Reason is null ? "" : $": \"{a.Reason}\""),

        IfAction a => $"If {Describe(a.Condition)}",

        ForEachAction a =>
            $"For each item in ${{{a.Source}}} as ${{{a.ItemVariable}}} "
            + $"(at most {a.MaxIterations ?? 25})",

        _ => throw new NotSupportedException(
            $"No rendering for action type {action.GetType().Name}. Add one to PlanRenderer "
            + "— a primitive was added to the DSL without teaching the renderer about it."),
    };

    /// <summary>Describe a postcondition, phrased as what the engine will check.</summary>
    public static string Describe(Postcondition expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);

        var within = expectation.WithinMs is { } ms ? $" within {ms} ms" : "";

        return expectation switch
        {
            ProcessRunningExpectation e => $"process \"{e.ProcessName}\" is running{within}",
            WindowExistsExpectation e => $"{Describe(e.Selector)} exists{within}",
            PathExistsExpectation e => $"{e.Path} exists{within}",
            ForegroundProcessIsExpectation e =>
                $"\"{e.ProcessName}\" owns the foreground window{within}",
            ClipboardMatchesExpectation e =>
                e.Exactly is not null
                    ? $"the clipboard equals \"{Ellipsis(e.Exactly)}\"{within}"
                    : $"the clipboard contains \"{Ellipsis(e.Contains ?? "")}\"{within}",
            _ => throw new NotSupportedException(
                $"No rendering for postcondition {expectation.GetType().Name}."),
        };
    }

    /// <summary>Describe a condition in plain English.</summary>
    public static string Describe(Condition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return condition switch
        {
            AllOfCondition c => string.Join(" and ", c.Conditions.Select(Describe)),
            AnyOfCondition c => string.Join(" or ", c.Conditions.Select(Describe)),
            SimplePredicate p => DescribeSimple(p),
            _ => throw new NotSupportedException(
                $"No rendering for condition {condition.GetType().Name}."),
        };
    }

    private static string DescribeSimple(SimplePredicate predicate)
    {
        var negated = predicate.Negate == true;

        return predicate switch
        {
            ProcessRunningPredicate p =>
                $"process \"{p.ProcessName}\" is {(negated ? "not " : "")}running",
            WindowExistsPredicate p =>
                $"{Describe(p.Selector)} does {(negated ? "not " : "")}exist",
            PathExistsPredicate p =>
                $"{p.Path} does {(negated ? "not " : "")}exist",
            VariableEqualsPredicate p =>
                $"${{{p.Variable}}} {(negated ? "is not" : "is")} \"{p.Value}\"",
            VariableEmptyPredicate p =>
                $"${{{p.Variable}}} is {(negated ? "not " : "")}empty",
            _ => throw new NotSupportedException(
                $"No rendering for predicate {predicate.GetType().Name}."),
        };
    }

    /// <summary>Describe a window selector.</summary>
    public static string Describe(WindowSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var parts = new List<string>();

        if (selector.ProcessName is not null)
        {
            parts.Add($"process \"{selector.ProcessName}\"");
        }

        if (selector.TitleContains is not null)
        {
            parts.Add($"title containing \"{selector.TitleContains}\"");
        }

        if (selector.TitleRegex is not null)
        {
            parts.Add($"title matching /{selector.TitleRegex}/");
        }

        if (selector.ClassName is not null)
        {
            parts.Add($"class \"{selector.ClassName}\"");
        }

        return parts.Count == 0
            ? "a window"
            : $"the window with {string.Join(" and ", parts)}";
    }

    /// <summary>Render a trigger chord, e.g. "Ctrl + Alt + P".</summary>
    public static string DescribeTrigger(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return Chord(trigger.Keys);
    }

    private static string Chord(IReadOnlyList<KeyName> keys) =>
        string.Join(" + ", keys.Select(Friendly));

    private static string Friendly(KeyName key)
    {
        var wire = Wire(key);

        // D0-D9 are the digit row; the wire names exist only because a C# identifier
        // cannot start with a digit.
        if (wire.Length == 2 && wire[0] == 'D' && char.IsAsciiDigit(wire[1]))
        {
            return wire[1..];
        }

        return wire switch
        {
            "CTRL" => "Ctrl",
            "ALT" => "Alt",
            "SHIFT" => "Shift",
            "WIN" => "Win",
            "OEM_COMMA" => ",",
            "OEM_PERIOD" => ".",
            "OEM_MINUS" => "-",
            "OEM_PLUS" => "+",
            _ => wire.Length == 1 ? wire : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                wire.ToLowerInvariant()),
        };
    }

    private static string Wire<TEnum>(TEnum value)
        where TEnum : struct, Enum => WireName.Of(value);

    private static string Ellipsis(string text, int max = 60) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    private static IEnumerable<HotkeyAction> Flatten(IEnumerable<HotkeyAction> actions)
    {
        foreach (var action in actions)
        {
            yield return action;

            var nested = action switch
            {
                IfAction a => a.Then.Concat(a.Else),
                ForEachAction a => a.Body,
                _ => [],
            };

            foreach (var child in Flatten(nested))
            {
                yield return child;
            }
        }
    }
}

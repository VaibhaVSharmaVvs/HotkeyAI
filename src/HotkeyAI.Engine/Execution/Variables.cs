using System.Text.RegularExpressions;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Engine.Execution;

/// <summary>
/// Variable storage and <c>${…}</c> interpolation for one run.
/// </summary>
/// <remarks>
/// Interpolation is textual substitution into DSL fields, never into anything a shell would
/// parse. <c>argv</c> is a list of separate arguments and there is no shell primitive, so a
/// variable holding <c>"; rm -rf /"</c> is just an argument containing those characters. That
/// property comes from the DSL's design, and this type must not undermine it — nothing here
/// concatenates values into a command line.
/// </remarks>
public sealed partial class Variables
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?:\.([A-Za-z0-9_]+))?\}")]
    private static partial Regex Reference { get; }

    private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariableType> declared;

    /// <summary>
    /// Variables holding something the user did not put in the plan.
    /// </summary>
    /// <remarks>
    /// <c>get_clipboard</c> and <c>type_text</c> were carefully kept out of the log, and then
    /// <c>abort.reason</c> interpolated the same clipboard text into a step detail — which becomes
    /// the transcript, the file under <c>%LOCALAPPDATA%\HotkeyAI\logs</c>, and the repair prompt
    /// PLAN.md expects people to paste somewhere. An AWS key and a password reached it that way.
    /// <para>
    /// Provenance is the fix rather than pattern-matching the value, because a secret does not look
    /// like anything in particular. What is known for certain is where it came from.
    /// </para>
    /// </remarks>
    private readonly HashSet<string> fromOutsideThePlan = new(StringComparer.Ordinal);

    public Variables(IEnumerable<VariableDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        declared = new Dictionary<string, VariableType>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            declared.TryAdd(declaration.Name, declaration.Type);
        }
    }

    /// <summary>Declared type of a variable, or null if it was never declared.</summary>
    public VariableType? TypeOf(string name) =>
        declared.TryGetValue(name, out var type) ? type : null;

    public void SetText(string name, string value) => Set(name, value);

    /// <summary>
    /// Store text that came from outside the plan, so it never reaches a log line.
    /// </summary>
    /// <remarks>
    /// The clipboard and a prompt answer are the two sources whose contents nobody chose to write
    /// down — see <see cref="fromOutsideThePlan"/>. A path the user picked is deliberately not in
    /// this category: <c>show_picker</c> already logs the chosen path as its own outcome, and a
    /// variable that redacted in one line while appearing in the one above it would be theatre.
    /// </remarks>
    public void SetTextFromOutsideThePlan(string name, string value)
    {
        values[name] = value;
        fromOutsideThePlan.Add(name);
    }

    public void SetPath(string name, string value) => Set(name, value);

    public void SetList(string name, IReadOnlyList<string> value) => Set(name, value);

    public void SetBoolean(string name, bool value) => Set(name, value);

    /// <summary>
    /// Store a value the plan itself produced, clearing any earlier outside-the-plan marking.
    /// </summary>
    /// <remarks>
    /// The clearing is the point. Reusing a name is legal, so a variable that held clipboard text
    /// and was later written by <c>path_exists</c> holds a boolean the plan computed — redacting
    /// that forever would make the log less useful for no gain.
    /// </remarks>
    private void Set(string name, object value)
    {
        values[name] = value;
        fromOutsideThePlan.Remove(name);
    }

    /// <summary>Remove a variable, used when a <c>foreach</c> item goes out of scope.</summary>
    public void Clear(string name)
    {
        values.Remove(name);
        fromOutsideThePlan.Remove(name);
    }

    public bool IsSet(string name) => values.ContainsKey(name);

    /// <summary>Read a list variable. Empty if unset.</summary>
    public IReadOnlyList<string> GetList(string name) =>
        values.TryGetValue(name, out var value) && value is IReadOnlyList<string> list ? list : [];

    /// <summary>Read a boolean variable, treating unset as false.</summary>
    public bool GetBoolean(string name) =>
        values.TryGetValue(name, out var value) && value is bool flag && flag;

    /// <summary>Read a scalar as text. Empty if unset.</summary>
    public string GetText(string name) =>
        values.TryGetValue(name, out var value) ? Render(value) : "";

    /// <summary>
    /// Substitute every <c>${name}</c> and <c>${name.property}</c> in a template.
    /// </summary>
    /// <remarks>
    /// An unknown or unset variable renders as empty rather than throwing. The policy layer
    /// already rejects plans that read undeclared or never-assigned variables, so reaching this
    /// case means either a conditional branch that did not run, or a plan that bypassed
    /// validation. Failing loudly mid-run would abort an automation the user is watching; the
    /// path guard and postconditions are what catch a value going wrong.
    /// </remarks>
    public string Interpolate(string? template) => Interpolate(template, redact: false);

    /// <summary>
    /// Substitute as <see cref="Interpolate(string?)"/> does, but redact anything that came from
    /// outside the plan.
    /// </summary>
    /// <remarks>
    /// For text that will be written down rather than acted on. <c>abort.reason</c> was the first
    /// field found putting clipboard contents into a file PLAN.md expects users to paste into
    /// repair prompts, and the path-bearing details were the rest. The placeholder names the
    /// variable, so the transcript still explains the shape of what happened — <c>${clip}</c>
    /// redacted is far more use than a blank.
    /// <para>
    /// Deliberately a separate method rather than a flag on the existing one. Interpolating for the
    /// desktop and interpolating for a log are different operations with different risks, and a
    /// default parameter would let the wrong one be reached by forgetting rather than by choosing.
    /// </para>
    /// </remarks>
    public string InterpolateForLog(string? template) => Interpolate(template, redact: true);

    private string Interpolate(string? template, bool redact)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${", StringComparison.Ordinal))
        {
            return template ?? "";
        }

        return Reference.Replace(template, match =>
        {
            var name = match.Groups[1].Value;

            if (!values.TryGetValue(name, out var value))
            {
                return "";
            }

            // Before rendering, and regardless of which property was asked for: ${clip.name} of
            // clipboard text is still clipboard text.
            if (redact && fromOutsideThePlan.Contains(name))
            {
                return $"[{name} redacted]";
            }

            return match.Groups[2].Success
                ? Property(value, match.Groups[2].Value)
                : Render(value);
        });
    }

    private static string Render(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IReadOnlyList<string> list => string.Join(", ", list),
        _ => value.ToString() ?? "",
    };

    private static string Property(object value, string property)
    {
        if (value is not string path)
        {
            return "";
        }

        return property switch
        {
            "name" => WindowsPath.FileName(path) ?? "",
            "fullPath" => path,
            "parent" => WindowsPath.Parent(path) ?? "",
            "extension" => WindowsPath.Extension(path) ?? "",
            _ => "",
        };
    }
}

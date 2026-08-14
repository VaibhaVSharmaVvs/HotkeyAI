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

    public void SetText(string name, string value) => values[name] = value;

    public void SetPath(string name, string value) => values[name] = value;

    public void SetList(string name, IReadOnlyList<string> value) => values[name] = value;

    public void SetBoolean(string name, bool value) => values[name] = value;

    /// <summary>Remove a variable, used when a <c>foreach</c> item goes out of scope.</summary>
    public void Clear(string name) => values.Remove(name);

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
    public string Interpolate(string? template)
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

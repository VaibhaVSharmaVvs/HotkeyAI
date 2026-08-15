namespace HotkeyAI.Core.Policy;

/// <summary>
/// The logical application names a plan may use in <c>launch_process</c>.
/// </summary>
/// <remarks>
/// <para>
/// Naming a logical application rather than a path is what lets a plan survive a machine
/// change or an app update, because the engine resolves the executable at run time. This type
/// owns the <i>names</i>; resolving one to an actual executable needs the Windows registry and
/// the filesystem, so it belongs in the agent, not here — Core stays free of Windows
/// dependencies.
/// </para>
/// <para>
/// This list is the source of truth. The schema's <c>app</c> description repeats it as prompt
/// material for whatever authors a plan, and a conformance test asserts the two agree — a
/// planner told an app is available when it is not will produce plans that fail at run time
/// for no visible reason.
/// </para>
/// </remarks>
public static class AppRegistry
{
    /// <summary>Logical names an automation may reference.</summary>
    public static IReadOnlySet<string> KnownApps { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vscode",
            "explorer",
            "chrome",
            "edge",
            "firefox",
            "terminal",
            "powershell",
            "notepad",
            "spotify",
            "slack",
            "teams",
            "discord",
            "cursor",
            "outlook",
            "obsidian",
        };

    /// <summary>True if the engine knows how to resolve this logical name.</summary>
    public static bool IsKnown(string app) => KnownApps.Contains(app);
}

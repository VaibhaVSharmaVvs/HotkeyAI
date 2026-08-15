namespace HotkeyAI.Windows;

/// <summary>
/// Where the agent keeps its state.
/// </summary>
/// <remarks>
/// Shared because two executables read it. The agent owns this data at runtime, but the CLI is
/// where anything console-shaped lives — listing automations, approving them, turning autostart
/// on — so both need to agree on the locations. Two copies of these strings would drift, and the
/// symptom would be a CLI that approves an automation the agent never sees.
/// </remarks>
public static class AgentPaths
{
    /// <summary>Root of everything Hotkey AI stores for this user.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotkeyAI");

    /// <summary>The folder watched for automation plans.</summary>
    public static string Automations { get; } = Path.Combine(Root, "automations");

    /// <summary>DPAPI-protected approval records.</summary>
    public static string Approvals { get; } = Path.Combine(Root, "approvals.dat");

    /// <summary>Automations the user has switched off.</summary>
    public static string Disabled { get; } = Path.Combine(Root, "disabled.json");

    /// <summary>What the user says about whether each automation actually works.</summary>
    public static string Health { get; } = Path.Combine(Root, "health.json");

    /// <summary>Which hotkeys last registered successfully, and when.</summary>
    public static string HotkeyHistory { get; } = Path.Combine(Root, "hotkeys.json");

    /// <summary>Folder holding the agent's daily logs.</summary>
    public static string Logs { get; } = Path.Combine(Root, "logs");

    /// <summary>Today's log file.</summary>
    public static string LogForToday() =>
        Path.Combine(Logs, $"agent-{DateTimeOffset.Now:yyyy-MM-dd}.log");
}

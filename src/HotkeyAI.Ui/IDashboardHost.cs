namespace HotkeyAI.Ui;

/// <summary>One row in the dashboard.</summary>
/// <param name="FileName">Identity, and what the store keys on.</param>
/// <param name="Name">The plan's human name, or the file name if it has none.</param>
/// <param name="Chord">Rendered trigger, for display.</param>
/// <param name="State">Why it is or is not running, phrased for a person.</param>
/// <param name="IsEnabled">The user's switch.</param>
/// <param name="IsLive">Enabled, approved, valid, and holding its hotkey right now.</param>
/// <param name="NeedsApproval">True when reading and approving is what unblocks it.</param>
/// <param name="Preview">The rendered plan, shown before approving.</param>
public sealed record DashboardEntry(
    string FileName,
    string Name,
    string Chord,
    string State,
    bool IsEnabled,
    bool IsLive,
    bool NeedsApproval,
    string Preview);

/// <summary>
/// What the dashboard needs from the agent.
/// </summary>
/// <remarks>
/// An interface so the window stays a renderer. Everything here — the store, the hotkey host, the
/// registry — belongs to the agent, and none of it should be reachable from a WPF file. It also
/// means the dashboard can be driven by a fake in a test, which a window wired directly to
/// <c>AutomationStore</c> could not be.
/// </remarks>
public interface IDashboardHost
{
    /// <summary>Read the current state of every automation.</summary>
    IReadOnlyList<DashboardEntry> Load();

    /// <summary>Switch one on or off, and rebind hotkeys to match.</summary>
    void SetEnabled(string fileName, bool enabled);

    /// <summary>Record approval for the plan as it currently stands, and rebind.</summary>
    void Approve(string fileName);

    /// <summary>Re-read the folder and rebind everything.</summary>
    void Reload();

    /// <summary>Open the automations folder in Explorer.</summary>
    void OpenAutomationsFolder();

    /// <summary>Open today's log.</summary>
    void OpenLog();

    /// <summary>Whether Hotkey AI starts at login.</summary>
    bool AutostartEnabled { get; set; }

    /// <summary>The prompt to paste into Claude Code for a described automation.</summary>
    string BuildAuthoringPrompt(string description, string? hotkey);

    /// <summary>
    /// Validate a pasted plan without saving it.
    /// </summary>
    /// <returns>An empty list when it is valid, otherwise the problems.</returns>
    IReadOnlyList<string> ValidatePlan(string json);

    /// <summary>Render a pasted plan the way the preview and `cli explain` do.</summary>
    /// <returns>The rendered plan, or the reason it could not be rendered.</returns>
    string ExplainPlan(string json);

    /// <summary>
    /// Save a pasted plan into the automations folder.
    /// </summary>
    /// <returns>Null on success, or the reason it was refused.</returns>
    string? SavePlan(string json);
}

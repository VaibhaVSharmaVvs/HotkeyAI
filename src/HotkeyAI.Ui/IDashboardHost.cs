using HotkeyAI.Core.Dsl;

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
/// <param name="LastRun">A short description of the last run, or null if it has not run.</param>
/// <param name="Health">The user's verdict on whether it works.</param>
/// <param name="HealthNote">What they said was wrong, if they said anything.</param>
public sealed record DashboardEntry(
    string FileName,
    string Name,
    string Chord,
    string State,
    bool IsEnabled,
    bool IsLive,
    bool NeedsApproval,
    string Preview,
    string? LastRun = null,
    HealthState Health = HealthState.Untested,
    string? HealthNote = null);

/// <summary>
/// Whether a person has confirmed an automation does what they meant.
/// </summary>
/// <remarks>
/// Not the same claim as the engine's "unverified", which is about whether a single action could
/// be checked. This is about whether the automation as a whole did the right thing, which no
/// amount of postconditions can establish.
/// </remarks>
public enum HealthState
{
    /// <summary>Never confirmed, or confirmed against a version that has since changed.</summary>
    Untested,

    /// <summary>The user has run it and says it does what they wanted.</summary>
    Works,

    /// <summary>The user has run it and says it does not.</summary>
    NotWorking,
}

/// <summary>One past version of a plan, for the history list.</summary>
/// <param name="Id">Opaque identifier, passed back to read or restore it.</param>
/// <param name="When">When this content was first seen.</param>
/// <param name="Summary">A short description for the list, such as when and how big.</param>
/// <param name="IsCurrent">True if this is what is on disk right now.</param>
public sealed record PlanVersionInfo(string Id, DateTimeOffset When, string Summary, bool IsCurrent);

/// <summary>What happened the last time an automation ran.</summary>
/// <param name="When">When it ran.</param>
/// <param name="Succeeded">Whether it finished without failing.</param>
/// <param name="Unverified">How many actions ran without the engine confirming any effect.</param>
/// <param name="Transcript">The execution log, verbatim.</param>
public sealed record RunRecord(
    DateTimeOffset When, bool Succeeded, int Unverified, string Transcript);

/// <summary>What probing a chord found out.</summary>
/// <param name="CanBind">Whether saving this chord would work.</param>
/// <param name="Message">What to tell the user, whether or not it can be bound.</param>
public sealed record HotkeyAvailability(bool CanBind, string Message);

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

    /// <summary>
    /// Report whether a chord could be bound, without binding it.
    /// </summary>
    /// <remarks>
    /// Called on every keypress while capturing, so the user finds out before committing rather
    /// than after saving.
    /// </remarks>
    HotkeyAvailability CheckHotkey(string fileName, IReadOnlyList<KeyName> keys);

    /// <summary>
    /// Rebind an automation to a new chord.
    /// </summary>
    /// <returns>Null on success, or the reason it was refused.</returns>
    string? SetHotkey(string fileName, IReadOnlyList<KeyName> keys);

    /// <summary>Past versions of a plan, newest first.</summary>
    IReadOnlyList<PlanVersionInfo> History(string fileName);

    /// <summary>The content of one past version, or null if it has been pruned.</summary>
    string? ReadVersion(string fileName, string versionId);

    /// <summary>The plan exactly as it is on disk right now.</summary>
    string? ReadCurrent(string fileName);

    /// <summary>
    /// Put a past version back.
    /// </summary>
    /// <returns>Null on success, or the reason it was refused.</returns>
    string? RestoreVersion(string fileName, string versionId);

    /// <summary>
    /// The automation a pasted plan would collide with, or null if it would be new.
    /// </summary>
    /// <remarks>
    /// Asked before saving, so a repaired plan coming back from a model is offered as a reviewable
    /// replacement rather than refused for having the same name as the thing it repairs.
    /// </remarks>
    string? ExistingFileFor(string json);

    /// <summary>
    /// Replace an existing automation with pasted JSON.
    /// </summary>
    /// <returns>Null on success, or the reason it was refused.</returns>
    string? ReplacePlan(string fileName, string json);

    /// <summary>
    /// Record whether the user says this automation works.
    /// </summary>
    /// <remarks>
    /// Deliberately does not affect whether it runs. You have to run an automation to find out
    /// whether it still misbehaves, and this must never become another reason a hotkey quietly
    /// stops firing.
    /// </remarks>
    void SetHealth(string fileName, HealthState state, string? note);

    /// <summary>
    /// What happened the last time this automation ran, or null if it has not run.
    /// </summary>
    /// <remarks>
    /// Held in memory only. A run that happened before the agent was last started is in the log
    /// file, but reading a transcript back out of a text log means parsing it, and a repair
    /// prompt built from a misparsed log is worse than one that admits it has nothing.
    /// </remarks>
    RunRecord? LastRun(string fileName);

    /// <summary>
    /// The prompt to paste into Claude Code to get a broken automation fixed.
    /// </summary>
    /// <param name="fileName">Which automation.</param>
    /// <param name="complaint">What the user says went wrong.</param>
    string BuildRepairPrompt(string fileName, string complaint);

    /// <summary>
    /// Release every hotkey until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// Required for capture to work at all. Windows delivers a registered chord to the thread
    /// that registered it, never to the focused window — so while the agent holds Ctrl+Alt+X,
    /// pressing it runs that automation instead of reaching the capture box. The window would be
    /// blind to precisely the combinations it most needs to recognise: the ones already in use.
    /// <para>
    /// The panic key goes too, for the same reason: it has to be pressable to be reported as
    /// reserved. Capture is modal and brief, and everything comes back on dispose.
    /// </para>
    /// </remarks>
    IDisposable SuspendHotkeys();
}

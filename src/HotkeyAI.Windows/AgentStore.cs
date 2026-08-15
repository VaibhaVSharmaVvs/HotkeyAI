using HotkeyAI.Core.Policy;
using HotkeyAI.Engine.Store;

namespace HotkeyAI.Windows;

/// <summary>
/// The agent's store, configured the same way wherever it is opened.
/// </summary>
/// <remarks>
/// One factory because there are two callers: the agent, which owns this data at run time, and the
/// CLI, whose <c>list</c> and <c>approve</c> verbs read and write the same files. They had been
/// assembling it separately, and the CLI's copy was missing two of the four storages — so
/// switching an automation off in the dashboard left <c>hotkeyai list</c> still reporting it as
/// ready to run, and a verdict recorded there was invisible from the terminal.
/// <para>
/// Nothing about that failed loudly. Both processes read real files and reported real state; it
/// was simply a different subset of the truth in each, which is the kind of divergence that
/// survives until someone compares the two by hand.
/// </para>
/// </remarks>
public static class AgentStore
{
    /// <summary>
    /// The policy the agent runs under.
    /// </summary>
    /// <remarks>
    /// Allowed roots are the user's profile. A plan may reach anywhere the user could reach
    /// anyway; what the guard stops is a plan reaching outside it via a path assembled at run
    /// time, which static validation cannot see.
    /// </remarks>
    public static PolicyOptions Policy { get; } = PolicyOptions.Default with
    {
        AllowedRoots = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
    };

    /// <summary>Open the store over the agent's files.</summary>
    public static AutomationStore Open() => new(
        new DpapiApprovalStorage(AgentPaths.Approvals),
        Policy,
        new JsonDisabledStorage(AgentPaths.Disabled),
        new JsonHealthStorage(AgentPaths.Health));
}

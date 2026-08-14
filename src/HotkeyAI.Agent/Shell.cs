using System.Diagnostics;

namespace HotkeyAI.Agent;

/// <summary>Opening files and folders in whatever the user has associated with them.</summary>
internal static class Shell
{
    /// <summary>Open a path with the shell. Failures are logged, never thrown.</summary>
    /// <remarks>
    /// Every caller is a menu item or a button. A missing log file or a folder the user has since
    /// deleted must not take down the process that owns their hotkeys.
    /// </remarks>
    public static void Open(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
#pragma warning disable CA1031 // A menu item must never take the agent down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AgentLog.Line($"Could not open {target}: {ex.Message}");
        }
    }
}

using System.IO;
using HotkeyAI.Windows;

namespace HotkeyAI.Agent;

/// <summary>
/// Where the agent says what it did.
/// </summary>
/// <remarks>
/// Once the agent lives in the tray there is no console to print to, and an automation that fails
/// silently is indistinguishable from one that never fired — which is the single worst state this
/// product can be in, because the user has no way to tell whether the problem is the hotkey, the
/// plan, or the app it was aiming at. So every run transcript goes to a file the tray menu can
/// open. There is no console to fall back on — the agent is a windowed process — so this file is
/// the only record that exists.
/// </remarks>
public static class AgentLog
{
    private static readonly Lock Gate = new();

    /// <summary>The log file for today. One per day, so it stays readable by a person.</summary>
    public static string Path { get; } = AgentPaths.LogForToday();

    /// <summary>Write a line to the log, and to the console if one is attached.</summary>
    public static void Line(string message = "")
    {
        Append(message.Length == 0
            ? Environment.NewLine
            : $"{DateTimeOffset.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }

    /// <summary>Write pre-formatted text, such as an execution transcript, verbatim.</summary>
    public static void Raw(string text)
    {
        Append(text);
    }

    private static void Append(string text)
    {
        // Logging must never be the reason an automation fails, so a broken log is swallowed.
        // The console copy, if there is one, still gets through.
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(AgentPaths.Logs);
                File.AppendAllText(Path, text);
            }
        }
#pragma warning disable CA1031 // A failed write must not take down the agent.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

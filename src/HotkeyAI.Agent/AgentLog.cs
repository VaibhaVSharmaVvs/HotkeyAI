using System.IO;
using HotkeyAI.Windows;

namespace HotkeyAI.Agent;

/// <summary>
/// Where the agent says what it did.
/// </summary>
/// <remarks>
/// <para>
/// Once the agent lives in the tray there is no console to print to, and an automation that fails
/// silently is indistinguishable from one that never fired — which is the single worst state this
/// product can be in, because the user has no way to tell whether the problem is the hotkey, the
/// plan, or the app it was aiming at. So every run transcript goes to a file the tray menu can
/// open. There is no console to fall back on — the agent is a windowed process — so this file is
/// the only record that exists.
/// </para>
/// <para>
/// Security review 2026-08-17, finding L2: it also grew without limit. One file per day, kept
/// forever, each holding the window titles and file paths PLAN.md item 7 flags as
/// PII/confidential-adjacent. "The record that exists" and "the record that exists indefinitely" are
/// different things, and only the first is a requirement.
/// </para>
/// </remarks>
public static class AgentLog
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Bytes after which a day's log rolls to a numbered part.
    /// </summary>
    /// <remarks>
    /// Rolls rather than truncates: a transcript half-written is worse than one in a second file, and
    /// the failure a user is chasing is usually the most recent. Eight megabytes is far above a normal
    /// day — four days of real use here came to 106 KB — so this catches a runaway loop logging
    /// thousands of steps, not ordinary growth.
    /// </remarks>
    private const long MaxBytesPerFile = 8L * 1024 * 1024;

    private static string? currentPath;
    private static DateOnly currentDate;
    private static long currentBytes;

    /// <summary>The log file being written now.</summary>
    /// <remarks>
    /// A property rather than a value computed once at type load. It used to be the latter, which
    /// meant an agent left running past midnight kept appending to yesterday's file — so "one per
    /// day" was true only of agents that were restarted daily.
    /// </remarks>
    public static string Path
    {
        get
        {
            lock (Gate)
            {
                return Resolve();
            }
        }
    }

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

    /// <summary>Which file to write to, rolling on a new day or a full one. Caller holds the lock.</summary>
    private static string Resolve()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (currentPath is not null && currentDate == today && currentBytes < MaxBytesPerFile)
        {
            return currentPath;
        }

        if (currentPath is null || currentDate != today)
        {
            // First write of the process, or the clock has crossed midnight. Seed the byte count
            // from what is already on disk so a restart does not reset the cap.
            currentDate = today;
            currentPath = AgentPaths.LogForToday();
            currentBytes = Length(currentPath);

            if (currentBytes < MaxBytesPerFile)
            {
                return currentPath;
            }
        }

        // Full. Find the next unused part for today.
        var basePath = AgentPaths.LogForToday();
        var stem = basePath[..^".log".Length];

        for (var part = 2; part < 1000; part++)
        {
            var candidate = $"{stem}.{part}.log";
            var length = Length(candidate);

            if (length < MaxBytesPerFile)
            {
                currentPath = candidate;
                currentBytes = length;
                return candidate;
            }
        }

        // A thousand full parts in one day is not a log problem any more. Keep writing to the last
        // one rather than losing the record entirely.
        currentPath = $"{stem}.999.log";
        currentBytes = 0;
        return currentPath;
    }

    private static long Length(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
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

                var path = Resolve();
                File.AppendAllText(path, text);

                // Counted rather than re-read. A FileInfo per line would turn every log write into a
                // filesystem round trip, and the count only has to be right enough to roll.
                currentBytes += text.Length;
            }
        }
#pragma warning disable CA1031 // A failed write must not take down the agent.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

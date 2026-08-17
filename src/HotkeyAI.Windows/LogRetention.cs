namespace HotkeyAI.Windows;

/// <summary>
/// How long the agent's logs are kept.
/// </summary>
/// <remarks>
/// <para>
/// Security review 2026-08-17, finding L2. Logs accumulated one file per day, kept for as long as the
/// machine lasted, each holding the window titles and file paths PLAN.md item 7 flags as
/// PII/confidential-adjacent under SOC2/ISO 27001. "The record that exists" and "the record that
/// exists indefinitely" are different things, and only the first was ever a requirement.
/// </para>
/// <para>
/// Beside <see cref="AgentPaths"/> rather than in the agent, because that is what already decides
/// where logs live — and because the rule is a pure decision about file names and dates, which is
/// the part worth testing.
/// </para>
/// </remarks>
public static class LogRetention
{
    /// <summary>
    /// How long a day's log is kept.
    /// </summary>
    /// <remarks>
    /// Two weeks, chosen against what the log is for: diagnosing an automation that misbehaved, and
    /// pasting a transcript into a repair prompt. Both happen within days of the run. Anything older
    /// is a liability with no reader.
    /// </remarks>
    public static TimeSpan Window { get; } = TimeSpan.FromDays(14);

    /// <summary>Delete logs past their retention, returning how many went.</summary>
    /// <remarks>
    /// Called at agent startup rather than on a timer. The agent is long-lived but it is also
    /// restarted often enough — every sign-in, every update — and a timer would be a second thing to
    /// get wrong for a job with no deadline.
    /// </remarks>
    public static int Prune() => Prune(AgentPaths.Logs, DateOnly.FromDateTime(DateTime.Now));

    /// <summary>Prune a named folder as of a given day, so the rule can be tested.</summary>
    /// <param name="folder">Folder holding <c>agent-*.log</c> files.</param>
    /// <param name="today">The day to measure age from.</param>
    /// <remarks>
    /// Age comes from the file name, not the timestamp: a backup, a copy or a sync rewrites the
    /// timestamp, and the name is what the log actually means.
    /// </remarks>
    public static int Prune(string folder, DateOnly today)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var cutoff = today.AddDays(-(int)Window.TotalDays);
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "agent-*.log"))
        {
            if (DateFromName(file) is not { } stamp || stamp >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held open by something, or not ours to delete. Retention is best-effort: failing
                // to tidy must not stop the agent starting.
            }
        }

        return removed;
    }

    /// <summary>
    /// The day a log file is for, from its name, or null if the name is not one of ours.
    /// </summary>
    /// <remarks>
    /// Handles both <c>agent-2026-08-17.log</c> and the rolled <c>agent-2026-08-17.2.log</c>. Anything
    /// else in the folder is left alone — deleting a file whose name this code does not understand is
    /// how a log folder someone repurposed loses something that mattered.
    /// </remarks>
    public static DateOnly? DateFromName(string path)
    {
        var name = Path.GetFileName(path);

        if (!name.StartsWith("agent-", StringComparison.Ordinal))
        {
            return null;
        }

        var stamp = name["agent-".Length..];

        return stamp.Length >= 10
               && DateOnly.TryParseExact(
                   stamp[..10],
                   "yyyy-MM-dd",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None,
                   out var parsed)
            ? parsed
            : null;
    }
}

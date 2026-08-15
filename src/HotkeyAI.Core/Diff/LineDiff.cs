namespace HotkeyAI.Core.Diff;

/// <summary>What happened to a line between two versions of a plan.</summary>
public enum DiffKind
{
    /// <summary>Present in both, unchanged.</summary>
    Same,

    /// <summary>Only in the new version.</summary>
    Added,

    /// <summary>Only in the old version.</summary>
    Removed,
}

/// <summary>One line of a rendered diff.</summary>
/// <param name="Kind">Whether it was added, removed, or left alone.</param>
/// <param name="Text">The line itself, without a trailing newline.</param>
public readonly record struct DiffLine(DiffKind Kind, string Text);

/// <summary>
/// A line-by-line diff between two versions of a plan.
/// </summary>
/// <remarks>
/// Exists because the concept shows only the new plan, and a new plan on its own is not
/// reviewable. When an automation comes back from a model — repaired, or rewritten — the question
/// worth answering is not "is this a reasonable plan?" but "what did it change?". Those have very
/// different answers when the model quietly dropped a step, and only the second one catches it.
/// <para>
/// A plain longest-common-subsequence diff, in Core, so it is a pure function with tests that run
/// on Linux CI and the WPF side merely paints the result. Plans are tens of lines, so the
/// quadratic table is not worth avoiding; the cap below is a guard against a pathological input
/// rather than an optimisation.
/// </para>
/// </remarks>
public static class LineDiff
{
    /// <summary>Largest input either side may have before the diff gives up.</summary>
    /// <remarks>
    /// A plan is tens of lines. Something thousands of lines long is not a plan, and a diff view
    /// that hangs on it would be a worse answer than one that declines.
    /// </remarks>
    private const int MaxLines = 4000;

    /// <summary>Diff two texts by line.</summary>
    /// <returns>Every line of both versions, in order, tagged with what happened to it.</returns>
    public static IReadOnlyList<DiffLine> Between(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var old = Split(before);
        var now = Split(after);

        if (old.Length > MaxLines || now.Length > MaxLines)
        {
            return
            [
                new DiffLine(DiffKind.Removed, $"({old.Length} lines, too long to diff)"),
                new DiffLine(DiffKind.Added, $"({now.Length} lines, too long to diff)"),
            ];
        }

        // lengths[i, j] is the longest common subsequence of old[i..] and now[j..].
        var lengths = new int[old.Length + 1, now.Length + 1];

        for (var i = old.Length - 1; i >= 0; i--)
        {
            for (var j = now.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(old[i], now[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(old.Length + now.Length);
        var x = 0;
        var y = 0;

        while (x < old.Length && y < now.Length)
        {
            if (string.Equals(old[x], now[y], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffKind.Same, old[x]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                // Removals before additions at the same position, so a changed line reads as the
                // old value struck out and the new one beneath it rather than the reverse.
                result.Add(new DiffLine(DiffKind.Removed, old[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, now[y]));
                y++;
            }
        }

        while (x < old.Length)
        {
            result.Add(new DiffLine(DiffKind.Removed, old[x++]));
        }

        while (y < now.Length)
        {
            result.Add(new DiffLine(DiffKind.Added, now[y++]));
        }

        return result;
    }

    /// <summary>How many lines were added and removed.</summary>
    /// <remarks>
    /// The headline a reviewer reads first. "+2 −1" on a repair is reassuring; "+40 −38" on the
    /// same repair means the model rewrote the plan rather than fixing it, and that is worth
    /// knowing before reading a line of it.
    /// </remarks>
    public static (int Added, int Removed) Summarise(IEnumerable<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var added = 0;
        var removed = 0;

        foreach (var line in lines)
        {
            if (line.Kind == DiffKind.Added)
            {
                added++;
            }
            else if (line.Kind == DiffKind.Removed)
            {
                removed++;
            }
        }

        return (added, removed);
    }

    private static string[] Split(string text) =>
        text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}

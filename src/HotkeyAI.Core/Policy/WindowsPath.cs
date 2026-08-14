using System.Text;

namespace HotkeyAI.Core.Policy;

/// <summary>
/// Windows path semantics, implemented independently of the host OS.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately does not use <see cref="System.IO.Path"/>. Those APIs follow the semantics of
/// the machine running the code, and Core is built and tested on Linux in CI while the plans
/// it validates always target Windows. <c>Path.IsPathFullyQualified(@"C:\Windows")</c> is
/// false on Linux, so the containment check would quietly disagree with itself between CI and
/// production — the worst possible failure mode for a security boundary.
/// </para>
/// <para>
/// This is a security boundary: it is what stops a plan launching an executable outside the
/// roots the user allowed. It resolves <c>..</c> before comparing, so
/// <c>C:\Projects\..\Windows\system32\cmd.exe</c> does not pass as being under
/// <c>C:\Projects</c>.
/// </para>
/// </remarks>
public static class WindowsPath
{
    /// <summary>True if the path is a drive-rooted or UNC absolute path.</summary>
    public static bool IsAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // UNC: \\server\share
        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return true;
        }

        // Drive-rooted: C:\ or C:/
        return path.Length >= 3
               && char.IsAsciiLetter(path[0])
               && path[1] == ':'
               && IsSeparator(path[2]);
    }

    /// <summary>
    /// Normalise for comparison: unify separators, resolve <c>.</c> and <c>..</c>, drop a
    /// trailing separator, and lower-case (Windows paths are case-insensitive).
    /// </summary>
    /// <returns>The normalised path, or null if it escapes above its root.</returns>
    public static string? Normalise(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var unc = path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        var stack = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Refusing to normalise a path that climbs above its own root is what makes
                // the containment check safe against traversal.
                if (stack.Count == 0)
                {
                    return null;
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(segment);
        }

        if (stack.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        if (unc)
        {
            text.Append("\\\\");
        }

        text.Append(string.Join('\\', stack));
        return text.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// True if <paramref name="candidate"/> is <paramref name="root"/> or sits beneath it.
    /// </summary>
    /// <remarks>
    /// Compares whole segments, so <c>C:\Projects-Secret</c> is not treated as being under
    /// <c>C:\Projects</c>.
    /// </remarks>
    public static bool IsUnder(string candidate, string root)
    {
        var normalisedCandidate = Normalise(candidate);
        var normalisedRoot = Normalise(root);

        if (normalisedCandidate is null || normalisedRoot is null)
        {
            return false;
        }

        if (string.Equals(normalisedCandidate, normalisedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        return normalisedCandidate.StartsWith(normalisedRoot + '\\', StringComparison.Ordinal);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}

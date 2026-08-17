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

            // Trimmed only after the "." and ".." tests above, never before: Effective leaves those
            // two alone, but relying on that here would make the order look incidental when it is
            // load-bearing. The containment check has to compare the path
            // Windows will act on, which is the one with trailing dots and spaces gone.
            stack.Add(Effective(segment));
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

    /// <summary>The final segment: file or folder name including any extension.</summary>
    /// <remarks>
    /// Backs <c>${variable.name}</c>. Implemented here rather than with
    /// <see cref="System.IO.Path"/> for the same reason as the rest of this type — on Linux,
    /// <c>Path.GetFileName(@"C:\a\b")</c> returns the whole string, because a backslash is an
    /// ordinary character there. An automation interpolating a path property would then produce
    /// different text in CI than on the machine it runs on.
    /// </remarks>
    public static string? FileName(string path)
    {
        var segments = Segments(path);
        return segments.Length == 0 ? null : segments[^1];
    }

    /// <summary>The containing directory, or null if the path has no parent.</summary>
    public static string? Parent(string path)
    {
        var segments = Segments(path);
        if (segments.Length <= 1)
        {
            return null;
        }

        var unc = path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);
        return (unc ? "\\\\" : "") + string.Join('\\', segments[..^1]);
    }

    /// <summary>The extension including the leading dot, or null if there is none.</summary>
    public static string? Extension(string path)
    {
        var name = FileName(path);
        if (name is null)
        {
            return null;
        }

        var dot = name.LastIndexOf('.');

        // A leading dot is a dotfile, not an extension.
        return dot <= 0 || dot == name.Length - 1 ? null : name[dot..];
    }

    private static string[] Segments(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : [.. path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Effective)];

    /// <summary>
    /// One path segment as Windows will actually interpret it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Win32 strips trailing dots and spaces from a path component before acting on it, and this
    /// type did not — so the two disagreed about what a path meant. <c>Extension("pwn.bat.")</c>
    /// returned null and <c>Extension("pwn.bat ")</c> returned <c>".bat "</c>, neither of which is
    /// in the executable blocklist, while Windows ran <c>pwn.bat</c> either way. That defeated the
    /// executable blocklist entirely, and the trailing-space spelling also defeated the preview's
    /// honesty: it renders <c>Open …\pwn.bat  with its default application</c> — note the double
    /// space, which is the whole problem, because a trailing space is invisible to whoever is
    /// approving it.
    /// </para>
    /// <para>
    /// Verified against the live filesystem before writing this: <c>target.txt.</c>,
    /// <c>target.txt </c> and <c>target.txt...</c> all open <c>target.txt</c>.
    /// </para>
    /// <para>
    /// A segment that is nothing *but* dots and spaces is left alone. <c>.</c> and <c>..</c> have
    /// meanings this type depends on — trimming them to nothing would break traversal detection,
    /// which is a far worse bug than the one being fixed. (<c>.. </c> was checked too, in case the
    /// same stripping turned it into a parent reference and opened a hole in the containment check:
    /// it does not. Windows treats it as an ordinary name, and no such directory exists.)
    /// </para>
    /// </remarks>
    private static string Effective(string segment)
    {
        var trimmed = segment.TrimEnd('.', ' ');
        return trimmed.Length == 0 ? segment : trimmed;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}

namespace HotkeyAI.Core.Policy;

/// <summary>
/// What <c>open_path</c> is allowed to hand to the shell.
/// </summary>
/// <remarks>
/// <para>
/// Security review 2026-08-17, finding M9. <c>open_path</c> was an unrestricted ShellExecute
/// bounded only by the path guard, and the guard's question is "is this under an allowed root" —
/// which for the default root, the user's profile, includes <c>Downloads</c> and
/// <c>AppData\Local\Temp</c>: every directory a browser or another process can drop a file into.
/// </para>
/// <para>
/// The shape the review demonstrated validates clean and is the whole problem: <c>list_files</c>
/// over <c>Downloads</c> with pattern <c>*</c>, then <c>foreach</c> → <c>open_path
/// ${f.fullPath}</c>. An automation the user approved as "open my downloads" executes whatever an
/// attacker put there, and the preview they approved said nothing that would have warned them.
/// </para>
/// <para>
/// This is an extension policy, not a content check. It does not stop a malicious PDF from
/// exploiting a PDF reader — nothing at this layer can. It stops the shell from being asked to
/// *execute* something, which is the difference between "open my documents" going wrong and
/// "open my documents" being arbitrary code execution.
/// </para>
/// </remarks>
public static class ShellOpen
{
    /// <summary>
    /// Extensions Windows treats as executable, in one form or another.
    /// </summary>
    /// <remarks>
    /// Longer than the obvious four, because the interesting entries are the ones people forget.
    /// <c>.lnk</c> and <c>.url</c> point at something else and are executed by opening them.
    /// <c>.scr</c> is a PE file. <c>.cpl</c> is a DLL the shell will load. <c>.reg</c> and
    /// <c>.msi</c> change the machine on a double click. <c>.hta</c> runs script with full trust,
    /// and <c>.jse</c>/<c>.wsf</c>/<c>.vbs</c> are the classic mail-worm formats that still work.
    /// <para>
    /// A blocklist, which is the shape this control cannot avoid: an allowlist of openable document
    /// types would refuse most of what a real automation legitimately opens, and a folder — which is
    /// what nearly every plan in the corpus opens — has no extension at all.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> ExecutableExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".bat", ".cmd", ".pif", ".scr", ".msi", ".msp", ".msc", ".cpl",
            ".reg", ".lnk", ".url", ".hta", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".ws", ".ps1", ".psm1", ".psc1", ".ps1xml", ".jar", ".gadget", ".application",
            ".appref-ms", ".chm", ".inf", ".sct", ".shb", ".shs", ".mst", ".cer", ".iso", ".img",
        };

    /// <summary>
    /// Whether the shell may be asked to open this path.
    /// </summary>
    /// <param name="path">A resolved path. A directory, or a path with no extension, is fine.</param>
    /// <param name="reason">Why it was refused, phrased for the person reading the log.</param>
    public static bool IsAllowed(string? path, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var extension = WindowsPath.Extension(path);

        if (extension is null || !ExecutableExtensions.Contains(extension))
        {
            return true;
        }

        reason =
            $"\"{path}\" is a {extension} file, which Windows executes rather than opens. "
            + "open_path hands a path to the shell, so this would run it. Use launch_process with a "
            + "logical app name if the intent is to start a program — that goes through the app "
            + "registry, which is checked.";

        return false;
    }
}

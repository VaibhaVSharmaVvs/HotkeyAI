using HotkeyAI.Core.Policy;

namespace HotkeyAI.Engine.Execution;

/// <summary>
/// Enforces the allowed roots against a path that has already been interpolated.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of safety control 2 the validator cannot provide. The policy layer checks
/// literal paths before a plan is stored, but a path built from a variable —
/// <c>${project}\.git</c> — has no value until the plan runs, and <c>open_path</c>,
/// <c>list_files</c> and <c>list_directories</c> legitimately interpolate. A check that
/// interpolation can slip past is not a boundary, so every filesystem and process operation
/// re-checks the resolved value here, immediately before touching the OS.
/// </para>
/// <para>
/// A refusal aborts the automation. Continuing past a path that fell outside the allowed roots
/// would mean reading or executing exactly what the boundary exists to prevent.
/// </para>
/// </remarks>
public sealed class PathGuard(IReadOnlyList<string> allowedRoots)
{
    private readonly IReadOnlyList<string> roots = allowedRoots ?? [];

    /// <summary>Whether a resolved path may be touched.</summary>
    /// <param name="path">The path after interpolation.</param>
    /// <param name="reason">Why it was refused, when it was.</param>
    public bool IsAllowed(string path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "The path resolved to nothing. A variable in it was probably never set.";
            return false;
        }

        if (path.Contains("${", StringComparison.Ordinal))
        {
            // Interpolation left a marker behind, so a variable was missing. Refusing beats
            // operating on a path containing a literal "${…}".
            reason = $"\"{path}\" still contains an unresolved variable.";
            return false;
        }

        if (!WindowsPath.IsAbsolute(path))
        {
            reason = $"\"{path}\" is not an absolute path.";
            return false;
        }

        if (WindowsPath.Normalise(path) is null)
        {
            // Normalise returns null when the path climbs above its own root.
            reason = $"\"{path}\" escapes above its root.";
            return false;
        }

        if (roots.Count == 0)
        {
            reason = "No allowed roots are configured, so no path may be used.";
            return false;
        }

        if (!roots.Any(root => WindowsPath.IsUnder(path, root)))
        {
            reason = $"\"{path}\" is outside the allowed roots ({string.Join(", ", roots)}).";
            return false;
        }

        reason = "";
        return true;
    }
}

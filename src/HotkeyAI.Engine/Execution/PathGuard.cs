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
public sealed class PathGuard(IReadOnlyList<string> allowedRoots, IRealPath? realPath = null)
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

        // Everything above is lexical, and deliberately so — it is what lets these rules be
        // tested identically on Linux CI. But a string comparison cannot see a reparse point: a
        // directory junction created inside the allowed root, which needs no elevation to make,
        // points anywhere on the machine while every path through it still reads as being under
        // the root. Security review 2026-08-17, finding H2, reproduced with a junction to
        // System32.
        //
        // The real damage was not reach — the default root is the whole user profile, so an
        // approved plan can already launch anything in it — but that the approval preview *lied*:
        // it showed an innocuous path in the profile while the launch landed in System32.
        if (realPath?.Resolve(path) is { } actual
            && !roots.Any(root => WindowsPath.IsUnder(actual, root)))
        {
            reason = $"\"{path}\" is a link to \"{actual}\", which is outside the allowed roots "
                   + $"({string.Join(", ", roots)}).";

            return false;
        }

        reason = "";
        return true;
    }
}

/// <summary>
/// Follows links so the guard can check where a path really goes.
/// </summary>
/// <remarks>
/// A seam rather than a call into <c>System.IO</c>, because this project keeps
/// <c>HotkeyAI.Engine</c> free of Windows dependencies so its safety controls can be tested on
/// Linux. Resolution is inherently a filesystem question, so it is supplied by the Windows layer
/// and simply absent in tests — where the lexical rules are what is under test.
/// </remarks>
public interface IRealPath
{
    /// <summary>
    /// Where a path actually leads once every link in it is followed.
    /// </summary>
    /// <returns>
    /// The resolved path, or null when it cannot be resolved — a path that does not exist yet, or
    /// one the process cannot open. Null means "no opinion": the lexical verdict stands, because
    /// refusing everything unresolvable would break paths that are simply not there yet.
    /// </returns>
    string? Resolve(string path);
}

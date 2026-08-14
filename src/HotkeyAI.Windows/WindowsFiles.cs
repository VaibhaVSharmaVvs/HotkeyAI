using System.Diagnostics;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Windows;

/// <summary>Filesystem reads and shell open.</summary>
/// <remarks>
/// Every path reaching here has already passed <c>PathGuard</c>, which checked the resolved
/// value against the allowed roots. This type does no policy of its own — one place decides what
/// is permitted, and it is not this one.
/// </remarks>
public sealed class WindowsFiles : IFiles
{
    public ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(
        string path, int depth, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<string>>(
            Enumerate(path, depth, files: false, pattern: null, cancellationToken));

    public ValueTask<IReadOnlyList<string>> ListFilesAsync(
        string path, string? pattern, int depth, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<string>>(
            Enumerate(path, depth, files: true, pattern, cancellationToken));

    public ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult(File.Exists(path) || Directory.Exists(path));

    public ValueTask OpenAsync(string path, CancellationToken cancellationToken)
    {
        // UseShellExecute is what makes this "open with whatever is associated", which is the
        // primitive's purpose. It is safe here precisely because the path was already checked:
        // the shell will happily launch anything, so the guard upstream is doing the work.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });

        return ValueTask.CompletedTask;
    }

    private static List<string> Enumerate(
        string path, int depth, bool files, string? pattern, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        var results = new List<string>();
        Walk(path, Math.Max(1, depth), files, pattern ?? "*", results, cancellationToken);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>
    /// Depth-limited walk that skips directories it cannot read.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than using <c>SearchOption.AllDirectories</c>, which throws on the
    /// first unreadable directory and abandons everything found so far. A user's Projects folder
    /// containing one locked directory should still list the other nineteen.
    /// </remarks>
    private static void Walk(
        string directory,
        int remaining,
        bool files,
        string pattern,
        List<string> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (remaining <= 0)
        {
            return;
        }

        try
        {
            if (files)
            {
                results.AddRange(Directory.EnumerateFiles(directory, pattern));
            }
            else
            {
                results.AddRange(Directory.EnumerateDirectories(directory));
            }

            if (remaining > 1)
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    Walk(child, remaining - 1, files, pattern, results, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or DirectoryNotFoundException
                                       or IOException)
        {
            // Skip and keep going.
        }
    }
}

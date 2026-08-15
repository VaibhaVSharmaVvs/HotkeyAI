using System.Globalization;

namespace HotkeyAI.Engine.Store;

/// <summary>One past version of a plan.</summary>
/// <param name="Id">Opaque identifier, and the file it is stored in.</param>
/// <param name="When">When this content was first seen.</param>
/// <param name="ContentHash">Hash of the content, matching the store's approval hashing.</param>
/// <param name="Lines">How many lines it has, for the history list.</param>
public sealed record PlanVersion(string Id, DateTimeOffset When, string ContentHash, int Lines);

/// <summary>Where past versions of a plan are kept.</summary>
public interface IVersionStore
{
    /// <summary>Record this content as the newest version, unless it already is.</summary>
    void Capture(string fileName, string content);

    /// <summary>Every kept version, newest first.</summary>
    IReadOnlyList<PlanVersion> History(string fileName);

    /// <summary>The content of one version, or null if it has been pruned.</summary>
    string? Read(string fileName, string versionId);
}

/// <summary>
/// Past versions of each plan, as plain files.
/// </summary>
/// <remarks>
/// PLAN.md called for a SQLite version table. This is deliberately not that. What has to be
/// stored is a few dozen copies of a few kilobytes of JSON, keyed by name and ordered by time —
/// which is what a directory is. SQLite would add a native dependency and a schema to migrate in
/// exchange for querying nobody needs, and the data would stop being readable with the tools the
/// rest of this project is already inspectable with. A database before there is a question it
/// answers is a liability.
/// <para>
/// The version *is* the file, so restoring is a copy and inspecting is opening it. That property
/// is worth more here than any query would be.
/// </para>
/// </remarks>
public sealed class FileVersionStore(string root, int keep = 20) : IVersionStore
{
    public void Capture(string fileName, string content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            var hash = AutomationStore.HashOf(content);

            // Nothing to record if this is already the newest. Reloads happen on every rebind and
            // on every folder change, so without this the history would fill with duplicates of
            // whatever the plan currently says.
            var history = History(fileName);

            if (history.Count > 0
                && string.Equals(history[0].ContentHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var directory = Folder(fileName);
            Directory.CreateDirectory(directory);

            // Sequence first, clock second. Ordering must not depend on a timestamp: captures are
            // fast enough to share a millisecond — reliably so on a quick filesystem — and the
            // order then fell through to the content hash, which is arbitrary. That surfaced as a
            // Linux-only CI failure, but the real cost would have been "restore the previous
            // version" restoring some other version. The timestamp stays in the name because it
            // is useful to a person reading the folder; nothing sorts by it.
            var next = history.Count > 0 ? SequenceOf(history[0].Id) + 1 : 1;
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            File.WriteAllText(
                Path.Combine(directory, $"v{next:D6}-{stamp}-{hash[..8]}.json"),
                content);

            Prune(directory);
        }
#pragma warning disable CA1031 // Losing history must never stop an automation loading.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    public IReadOnlyList<PlanVersion> History(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        try
        {
            var directory = Folder(fileName);

            if (!Directory.Exists(directory))
            {
                return [];
            }

            var versions = new List<PlanVersion>();

            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var content = File.ReadAllText(path);

                versions.Add(new PlanVersion(
                    Path.GetFileName(path),
                    File.GetLastWriteTime(path),
                    AutomationStore.HashOf(content),
                    content.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length));
            }

            return [.. Newest(versions, v => v.Id)];
        }
#pragma warning disable CA1031 // An unreadable history is an empty one, not a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    public string? Read(string fileName, string versionId)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(versionId);

        try
        {
            // The id comes from History and names a file in this folder. Reduced to its file name
            // first, so an id carrying a path cannot read outside the version folder.
            var path = Path.Combine(Folder(fileName), Path.GetFileName(versionId));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
#pragma warning disable CA1031 // A missing version reads as missing.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private void Prune(string directory)
    {
        var files = Newest(Directory.EnumerateFiles(directory, "*.json"), Path.GetFileName)
            .Skip(keep)
            .ToList();

        foreach (var file in files)
        {
            File.Delete(file);
        }
    }

    /// <summary>Newest first, by capture sequence.</summary>
    /// <remarks>
    /// The name breaks the tie only between versions written before this scheme existed, which
    /// carry no sequence and are therefore the oldest things here.
    /// </remarks>
    private static IEnumerable<T> Newest<T>(IEnumerable<T> items, Func<T, string> name) =>
        items
            .OrderByDescending(i => SequenceOf(name(i)))
            .ThenByDescending(i => name(i), StringComparer.Ordinal);

    /// <summary>
    /// The capture sequence encoded in a version's file name.
    /// </summary>
    /// <remarks>
    /// Returns -1 for a name written before versions were sequenced, so those sort below every
    /// sequenced one. They are older by definition, and a folder captured under the previous
    /// scheme keeps working rather than jumping to the top of the list forever.
    /// </remarks>
    private static long SequenceOf(string name)
    {
        if (!name.StartsWith('v'))
        {
            return -1;
        }

        var end = name.IndexOf('-', StringComparison.Ordinal);

        return end > 1 && long.TryParse(
            name.AsSpan(1, end - 1), CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : -1;
    }

    /// <summary>One folder per automation, named after it.</summary>
    private string Folder(string fileName) =>
        Path.Combine(root, Path.GetFileNameWithoutExtension(fileName));
}

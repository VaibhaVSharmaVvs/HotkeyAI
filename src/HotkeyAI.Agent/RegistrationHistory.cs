using System.Globalization;
using System.IO;
using System.Text.Json;

namespace HotkeyAI.Agent;

/// <summary>
/// Remembers which hotkeys worked, so a failure can be described rather than just reported.
/// </summary>
/// <remarks>
/// Phase 0 established that <c>RegisterHotKey</c>'s failure carries no diagnosis: a chord held by
/// another application and one reserved by the shell both return the same undifferentiated 1409,
/// and the API will never name the holder. That is a hard limit — no amount of probing gets past
/// it.
/// <para>
/// What the application can add is history. "This worked yesterday and does not today" is the one
/// genuinely useful thing to say here, because it separates *you have always had a conflict* from
/// *something changed on this machine*, and only the second is worth the user's time to chase.
/// It is the only diagnosis available that the raw API cannot provide.
/// </para>
/// </remarks>
public sealed class RegistrationHistory
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string path;
    private Dictionary<string, Entry> entries;

    public RegistrationHistory(string path)
    {
        this.path = path;
        entries = Read(path);
    }

    /// <summary>What was known to work, and when it last did.</summary>
    /// <param name="Chord">The rendered chord, so a changed binding is not mistaken for a loss.</param>
    /// <param name="LastRegistered">When it last registered successfully.</param>
    public sealed record Entry(string Chord, DateTimeOffset LastRegistered);

    /// <summary>
    /// Explain a failed registration in terms of what used to happen, or return null.
    /// </summary>
    /// <remarks>
    /// Returns null when there is nothing useful to add — a chord that has never worked gets the
    /// plain message, because inventing history for it would be worse than saying less.
    /// </remarks>
    public string? Explain(string name, string chord)
    {
        if (!entries.TryGetValue(name, out var previous)
            || !string.Equals(previous.Chord, chord, StringComparison.Ordinal))
        {
            return null;
        }

        var days = (DateTimeOffset.Now - previous.LastRegistered).TotalDays;

        var when = days switch
        {
            < 1 => "earlier today",
            < 2 => "yesterday",
            < 14 => $"{(int)days} days ago",
            _ => previous.LastRegistered.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };

        return $"it registered {when}, so something on this machine has taken it since";
    }

    /// <summary>Record a chord that registered successfully.</summary>
    public void RecordSuccess(string name, string chord) =>
        entries[name] = new Entry(chord, DateTimeOffset.Now);

    /// <summary>Persist the history. Failure is not fatal — this is a diagnostic aid.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries, Options));
        }
#pragma warning disable CA1031 // Losing history must never stop the agent from running.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static Dictionary<string, Entry> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path))
                  ?? []
                : [];
        }
#pragma warning disable CA1031 // A corrupt history file is not worth refusing to start over.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }
}

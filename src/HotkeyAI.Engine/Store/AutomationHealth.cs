using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotkeyAI.Engine.Store;

/// <summary>
/// Whether a person has confirmed this automation does what they meant.
/// </summary>
/// <remarks>
/// Deliberately not called "verified". That word already means something narrower and entirely
/// mechanical here: an <i>action</i> is unverified when it carries no postcondition, so the engine
/// ran it but cannot confirm it had any effect. This is the other half, and the half no amount of
/// engineering can supply — the engine can check effects, and only the user can say whether the
/// effects were the ones they wanted. Both claims appear side by side in the dashboard and in a
/// repair prompt, so they must not share a name.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AutomationHealth>))]
public enum AutomationHealth
{
    /// <summary>Never confirmed, or confirmed against a version that has since changed.</summary>
    Untested,

    /// <summary>The user has run it and says it does what they wanted.</summary>
    Works,

    /// <summary>The user has run it and says it does not.</summary>
    NotWorking,
}

/// <summary>What the user said, and about which version of the plan they said it.</summary>
/// <param name="State">Their verdict.</param>
/// <param name="ContentHash">
/// The plan this verdict applies to. A verdict about a plan that has since been edited is stale,
/// and is discarded rather than carried over — "I tested this" cannot survive the thing that was
/// tested being changed.
/// </param>
/// <param name="When">When they said it.</param>
/// <param name="Note">What they said was wrong, if anything. Feeds the repair prompt.</param>
public sealed record HealthRecord(
    AutomationHealth State,
    string ContentHash,
    DateTimeOffset When,
    string? Note = null);

/// <summary>Where the user's verdicts are kept.</summary>
/// <remarks>
/// Plain and unprotected, like the switched-off list and unlike approvals. This records an opinion,
/// not a permission: nothing here decides whether an automation may run.
/// </remarks>
public interface IHealthStorage
{
    IReadOnlyDictionary<string, HealthRecord> Read();

    void Write(IReadOnlyDictionary<string, HealthRecord> health);
}

/// <summary>The verdicts, as a plain JSON file.</summary>
public sealed class JsonHealthStorage(string path) : IHealthStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public IReadOnlyDictionary<string, HealthRecord> Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, HealthRecord>(StringComparer.OrdinalIgnoreCase);
            }

            var read = JsonSerializer.Deserialize<Dictionary<string, HealthRecord>>(
                File.ReadAllText(path), Options);

            return new Dictionary<string, HealthRecord>(
                read ?? [], StringComparer.OrdinalIgnoreCase);
        }
#pragma warning disable CA1031 // A corrupt opinion file must not stop automations loading.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new Dictionary<string, HealthRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Write(IReadOnlyDictionary<string, HealthRecord> health)
    {
        ArgumentNullException.ThrowIfNull(health);

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(health, Options));
        }
#pragma warning disable CA1031 // Failing to persist an opinion must not take the agent down.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

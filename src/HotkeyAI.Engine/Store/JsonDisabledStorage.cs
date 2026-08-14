using System.Text.Json;

namespace HotkeyAI.Engine.Store;

/// <summary>
/// The switched-off list, as a plain JSON file.
/// </summary>
/// <remarks>
/// Plain and unprotected, unlike approvals. This records a preference, not a permission — the
/// worst an attacker gains by editing it is re-enabling something the user already read and
/// approved, and anyone able to write here can write to the automations folder too, where
/// approval still stands in the way.
/// <para>
/// Stores the names that are <i>off</i> rather than the ones that are on, so an automation the
/// user drops in is enabled by default. Storing the inverse would mean every new plan arrived
/// silently switched off, and the user would be left looking for the reason its hotkey did
/// nothing.
/// </para>
/// </remarks>
public sealed class JsonDisabledStorage(string path) : IDisabledStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public IReadOnlySet<string> Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
#pragma warning disable CA1031 // A corrupt preference file must not stop automations loading.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Write(IReadOnlySet<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(disabled.Order(StringComparer.Ordinal).ToArray(), Options));
        }
#pragma warning disable CA1031 // Failing to persist a toggle must not take the agent down.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

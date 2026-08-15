using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotkeyAI.Core;
using HotkeyAI.Core.Authoring;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Core.Policy;
using HotkeyAI.Engine.Store;
using HotkeyAI.Ui;
using HotkeyAI.Windows;

namespace HotkeyAI.Agent;

/// <summary>
/// Connects the dashboard to the agent's store, hotkeys and settings.
/// </summary>
/// <remarks>
/// Everything the window can do goes through here, and everything that changes what may run ends
/// by rebinding hotkeys. A dashboard that reports an automation as on while the agent still holds
/// no chord for it would be worse than no dashboard, because the user would stop believing the
/// one screen that is supposed to tell them the truth.
/// </remarks>
internal sealed class DashboardHost(
    AutomationStore store,
    PolicyOptions policy,
    Action rebind,
    Func<IReadOnlyList<KeyName>, RegistrationResult> probe,
    Func<IDisposable> suspendHotkeys,
    IReadOnlyList<KeyName> panicChord) : IDashboardHost
{
    public IDisposable SuspendHotkeys() => suspendHotkeys();

    public bool AutostartEnabled
    {
        get => Autostart.IsEnabled();

        set
        {
            var error = value ? Autostart.Enable() : Autostart.Disable();

            if (error is not null)
            {
                AgentLog.Line($"Autostart change failed: {error}");
            }
        }
    }

    public IReadOnlyList<DashboardEntry> Load() =>
    [
        .. store.Load(AgentPaths.Automations).Select(a => new DashboardEntry(
            a.FileName,
            a.Plan?.Name is { Length: > 0 } name ? name : a.FileName,
            a.Plan is { } plan ? PlanRenderer.DescribeTrigger(plan.Trigger) : "—",
            a.Blocker ?? "live",
            a.IsEnabled,
            a.IsRunnable,
            a.Status != ApprovalStatus.Approved && a.Validation.IsValid && a.Plan is not null,
            a.Plan is { } p ? PlanRenderer.Explain(p) : Problems(a))),
    ];

    public void SetEnabled(string fileName, bool enabled)
    {
        store.SetEnabled(fileName, enabled);
        rebind();
    }

    public void Approve(string fileName)
    {
        var automation = store.Load(AgentPaths.Automations)
            .FirstOrDefault(a => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (automation is null)
        {
            return;
        }

        store.Approve(automation);
        rebind();
    }

    public void Reload() => rebind();

    public void OpenAutomationsFolder() => Shell.Open(AgentPaths.Automations);

    public void OpenLog() => Shell.Open(AgentLog.Path);

    public string BuildAuthoringPrompt(string description, string? hotkey) =>
        AuthoringPrompt.For(description ?? "", hotkey);

    public IReadOnlyList<string> ValidatePlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ["Paste the JSON first."];
        }

        var result = PlanValidator.Validate(json, policy);
        return result.IsValid ? [] : [.. result.Errors.Select(e => e.ToString())];
    }

    public string ExplainPlan(string json)
    {
        var problems = ValidatePlan(json);

        if (problems.Count > 0)
        {
            return string.Join(Environment.NewLine, problems.Take(5));
        }

        try
        {
            var plan = JsonSerializer.Deserialize<Automation>(json, DslJson.Options);
            return plan is null ? "The plan could not be read." : PlanRenderer.Explain(plan);
        }
        catch (JsonException ex)
        {
            return $"The plan could not be read: {ex.Message}";
        }
    }

    /// <summary>
    /// Write a pasted plan into the automations folder.
    /// </summary>
    /// <remarks>
    /// Validated before it is written, never after. A file that lands in the watched folder is
    /// something the store must classify and the user must eventually approve, and writing
    /// something known to be broken just to report it back would leave litter behind on every
    /// mistake.
    /// </remarks>
    public string? SavePlan(string json)
    {
        var problems = ValidatePlan(json);

        if (problems.Count > 0)
        {
            return string.Join(Environment.NewLine, problems.Take(5));
        }

        Automation plan;

        try
        {
            plan = JsonSerializer.Deserialize<Automation>(json, DslJson.Options)!;
        }
        catch (JsonException ex)
        {
            return $"The plan could not be read: {ex.Message}";
        }

        var path = Path.Combine(AgentPaths.Automations, $"{Slug(plan.Name)}.json");

        if (File.Exists(path))
        {
            return $"{Path.GetFileName(path)} already exists. Rename the automation, or edit the "
                   + "existing file in the automations folder.";
        }

        try
        {
            Directory.CreateDirectory(AgentPaths.Automations);
            File.WriteAllText(path, json.Trim() + Environment.NewLine);
        }
        catch (IOException ex)
        {
            return $"Could not save: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Could not save: {ex.Message}";
        }

        AgentLog.Line($"Saved a new automation: {Path.GetFileName(path)}");
        rebind();
        return null;
    }


    public HotkeyAvailability CheckHotkey(string fileName, IReadOnlyList<KeyName> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return new HotkeyAvailability(false, "Press a combination.");
        }

        // The same rule the validator applies, so the answer here cannot disagree with the
        // answer on save.
        if (HotkeyChord.Problems(keys) is { Count: > 0 } problems)
        {
            return new HotkeyAvailability(false, problems[0]);
        }

        var chord = HotkeyChord.Normalise(keys);
        var rendered = PlanRenderer.DescribeTrigger(new Trigger { Keys = chord });

        if (Same(chord, panicChord))
        {
            return new HotkeyAvailability(
                false, $"{rendered} is the panic key, which stops a running automation.");
        }

        // Checked before probing, and load-bearing rather than a nicety. While the capture window
        // is open every hotkey is released, so a chord another automation owns probes as *free* —
        // the store is the only remaining source of truth about our own bindings, and naming the
        // automation is the one diagnosis the API structurally cannot give anyway.
        foreach (var other in store.Load(AgentPaths.Automations))
        {
            if (other.Plan is not { } plan
                || string.Equals(other.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Same(chord, HotkeyChord.Normalise(plan.Trigger.Keys)))
            {
                var name = plan.Name is { Length: > 0 } n ? n : other.FileName;
                return new HotkeyAvailability(false, $"{rendered} is already used by {name}.");
            }
        }

        var current = store.Load(AgentPaths.Automations)
            .FirstOrDefault(a =>
                string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?.Plan;

        if (current is not null && Same(chord, HotkeyChord.Normalise(current.Trigger.Keys)))
        {
            return new HotkeyAvailability(true, $"{rendered} — already its hotkey.");
        }

        var result = probe(chord);

        // Deliberately not promising more than the OS knows. A chord held by a low-level
        // keyboard hook reports as free here and then never fires, so "available" is the
        // strongest honest word.
        return result.Registered
            ? new HotkeyAvailability(true, $"{rendered} is available.")
            : new HotkeyAvailability(false, $"{rendered} — {result.Describe()}");
    }

    /// <summary>
    /// Write a new trigger into the plan, carrying its approval across.
    /// </summary>
    /// <remarks>
    /// Approval is granted against the file's content hash, so changing the trigger revokes it —
    /// which is right when a file changes underneath the user, and wrong here. The user just
    /// asked for this change, in this window, and nothing about what the automation *does* has
    /// moved; making them re-read the whole plan to rebind a key would teach them to click past
    /// the one prompt that stops a dropped file running on a keypress. The approval is re-granted
    /// only because this code made the edit and knows it touched nothing else.
    /// </remarks>
    public string? SetHotkey(string fileName, IReadOnlyList<KeyName> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var availability = CheckHotkey(fileName, keys);

        if (!availability.CanBind)
        {
            return availability.Message;
        }

        var automation = store.Load(AgentPaths.Automations)
            .FirstOrDefault(a =>
                string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (automation is null)
        {
            return $"{fileName} is no longer in the automations folder.";
        }

        var wasApproved = automation.Status == ApprovalStatus.Approved;
        var chord = HotkeyChord.Normalise(keys);

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(automation.Path))
                ?? throw new JsonException("The plan is empty.");

            node["trigger"]!["keys"] = new JsonArray(
                [.. chord.Select(k => JsonValue.Create(WireName.Of(k)))]);

            File.WriteAllText(
                automation.Path,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                + Environment.NewLine);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return $"Could not rewrite {fileName}: {ex.Message}";
        }

        if (wasApproved)
        {
            var rewritten = store.Load(AgentPaths.Automations)
                .FirstOrDefault(a =>
                    string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));

            if (rewritten is not null && rewritten.Validation.IsValid)
            {
                store.Approve(rewritten);
            }
        }

        AgentLog.Line(
            $"{fileName} rebound to {PlanRenderer.DescribeTrigger(new Trigger { Keys = chord })}.");

        rebind();
        return null;
    }

    private static bool Same(IReadOnlyList<KeyName> a, IReadOnlyList<KeyName> b) =>
        a.Count == b.Count && a.SequenceEqual(b);

    private static string Problems(StoredAutomation automation) =>
        automation.Validation.Errors.Count == 0
            ? "This plan could not be read."
            : string.Join(
                Environment.NewLine,
                automation.Validation.Errors.Select(e => e.ToString()));

    /// <summary>Turn a plan name into a safe file name.</summary>
    private static string Slug(string? name)
    {
        var text = (name ?? "").Trim();
        var slug = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var result = slug.ToString().Trim('-');
        return result.Length == 0 ? "automation" : result;
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
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
    AutomationStore store, PolicyOptions policy, Action rebind) : IDashboardHost
{
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

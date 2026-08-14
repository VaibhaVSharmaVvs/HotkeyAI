using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HotkeyAI.Core;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Core.Policy;

namespace HotkeyAI.Engine.Store;

/// <summary>Whether the user has seen and approved this exact plan.</summary>
public enum ApprovalStatus
{
    /// <summary>Approved, and unchanged since.</summary>
    Approved,

    /// <summary>Never seen before.</summary>
    New,

    /// <summary>Approved once, but the file has changed since.</summary>
    Changed,
}

/// <summary>An automation as found on disk.</summary>
/// <param name="FileName">File name, which is the automation's identity.</param>
/// <param name="Path">Full path.</param>
/// <param name="Plan">The parsed plan, or null if it did not validate.</param>
/// <param name="Status">Approval state.</param>
/// <param name="Validation">Result of both validation layers.</param>
/// <param name="ContentHash">SHA-256 of the file, which is what approval is granted against.</param>
public sealed record StoredAutomation(
    string FileName,
    string Path,
    Automation? Plan,
    ApprovalStatus Status,
    ValidationResult Validation,
    string ContentHash)
{
    /// <summary>
    /// Whether this automation may have a hotkey registered and be allowed to run.
    /// </summary>
    /// <remarks>
    /// Both conditions, always. An unapproved plan is inert no matter how valid it is, and an
    /// approved plan that no longer validates is inert no matter how trusted it was.
    /// </remarks>
    public bool IsRunnable => Status == ApprovalStatus.Approved && Validation.IsValid && Plan is not null;

    /// <summary>Why it is not runnable, phrased for the user.</summary>
    public string? Blocker => Status switch
    {
        ApprovalStatus.New => "new — review the plan and approve it before it can run",
        ApprovalStatus.Changed =>
            "changed since you approved it — review the new plan and approve it again",
        _ when !Validation.IsValid => $"invalid — {Validation.Errors.Count} problem(s)",
        _ => null,
    };
}

/// <summary>Where approvals are kept. Implemented against DPAPI on Windows.</summary>
public interface IApprovalStorage
{
    /// <summary>File name to the content hash that was approved.</summary>
    IReadOnlyDictionary<string, string> Read();

    void Write(IReadOnlyDictionary<string, string> approvals);
}

/// <summary>
/// Loads automations from disk and decides which are allowed to run.
/// </summary>
/// <remarks>
/// <para>
/// Safety control 4, and the resolution of a genuine conflict. Signing automation files and
/// refusing tampered ones is right against a dropper that writes JSON into the folder — but
/// under V1's authoring model, <i>every</i> file the user legitimately writes is unsigned, so
/// enforcing that naively would make the intended workflow indistinguishable from an attack.
/// </para>
/// <para>
/// So nothing is refused. An unknown or changed plan is loaded and left <b>inert</b>: no hotkey
/// is registered for it and it cannot run until a person has read the rendered plan and
/// approved it. Approval is granted against the file's content hash, so a later edit reverts it
/// to inert automatically.
/// </para>
/// <para>
/// <b>What this does not do.</b> Approvals are protected at user scope, so malware already
/// running as this user could in principle read the same secret and forge an approval. This
/// raises the bar rather than closing the door — the control that actually matters is that a
/// human sees the plan before anything runs, and that survives regardless.
/// </para>
/// </remarks>
public sealed class AutomationStore(IApprovalStorage approvals, PolicyOptions? policy = null)
{
    private readonly PolicyOptions policy = policy ?? PolicyOptions.Default;

    /// <summary>Read every automation in a directory and classify it.</summary>
    public IReadOnlyList<StoredAutomation> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var approved = approvals.Read();
        var found = new List<StoredAutomation>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            found.Add(Classify(path, approved));
        }

        return found;
    }

    /// <summary>Record that the user approved this exact content.</summary>
    public void Approve(StoredAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);

        var updated = new Dictionary<string, string>(approvals.Read(), StringComparer.OrdinalIgnoreCase)
        {
            [automation.FileName] = automation.ContentHash,
        };

        approvals.Write(updated);
    }

    /// <summary>Withdraw approval, making the automation inert again.</summary>
    public void Revoke(string fileName)
    {
        var updated = new Dictionary<string, string>(approvals.Read(), StringComparer.OrdinalIgnoreCase);

        if (updated.Remove(fileName))
        {
            approvals.Write(updated);
        }
    }

    /// <summary>SHA-256 of a file's bytes, normalised for line endings.</summary>
    /// <remarks>
    /// Line endings are normalised so that an editor rewriting CRLF as LF — or git doing it on
    /// checkout — does not silently revoke an approval the user already gave. Only a change to
    /// what the plan actually says should count as a change.
    /// </remarks>
    public static string HashOf(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalised = content.ReplaceLineEndings("\n").Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    private StoredAutomation Classify(string path, IReadOnlyDictionary<string, string> approved)
    {
        var fileName = Path.GetFileName(path);
        string content;

        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return new StoredAutomation(
                fileName,
                path,
                null,
                ApprovalStatus.New,
                new ValidationResult([
                    new ValidationError(ValidationLayer.Schema, "", $"Could not read: {ex.Message}"),
                ]),
                "");
        }

        var hash = HashOf(content);

        var status = !approved.TryGetValue(fileName, out var previous)
            ? ApprovalStatus.New
            : string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase)
                ? ApprovalStatus.Approved
                : ApprovalStatus.Changed;

        var validation = PlanValidator.Validate(content, this.policy);

        Automation? plan = null;
        if (validation.IsValid)
        {
            try
            {
                plan = JsonSerializer.Deserialize<Automation>(content, DslJson.Options);
            }
            catch (JsonException)
            {
                plan = null;
            }
        }

        return new StoredAutomation(fileName, path, plan, status, validation, hash);
    }
}

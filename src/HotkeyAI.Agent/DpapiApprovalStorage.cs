using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HotkeyAI.Engine.Store;

namespace HotkeyAI.Agent;

/// <summary>
/// Approvals persisted under DPAPI at user scope.
/// </summary>
/// <remarks>
/// <para>
/// Encrypting rather than plain JSON stops the most likely attack on this file: something that
/// drops an automation into the folder and edits a text file to mark it approved. It does not
/// stop malware already running as this user, which can call the same DPAPI entry point — the
/// control that actually matters is that a person reads the plan before it can run, and that
/// holds regardless of what happens to this file.
/// </para>
/// <para>
/// A corrupt or undecryptable file is treated as empty rather than as an error. Failing to start
/// because approvals could not be read would be worse than the alternative, which is that every
/// automation reverts to inert and the user re-approves — the safe direction.
/// </para>
/// </remarks>
public sealed class DpapiApprovalStorage(string path) : IApprovalStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HotkeyAI.approvals.v1");

    public IReadOnlyDictionary<string, string> Read()
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Tampered, corrupt, or written by a different user profile. Everything becomes
            // inert, which is the direction that fails safe.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Write(IReadOnlyDictionary<string, string> approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plain = JsonSerializer.SerializeToUtf8Bytes(approvals);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        // Write-then-move, so an interrupted write cannot leave a half-written approvals file
        // that reads as corrupt and silently disables every automation.
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);
    }
}

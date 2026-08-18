using System.Security.AccessControl;
using System.Security.Principal;

namespace HotkeyAI.Windows;

/// <summary>
/// The per-user ACL on the agent's store, set explicitly and checkable afterwards.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md control 4 specifies a per-user ACL on the store. <c>%LOCALAPPDATA%</c> inherits a
/// reasonable one by default, so the control held in practice — but nothing used to set it and
/// nothing asserted it, and a control that is true by accident is one nobody notices becoming
/// false. An inherited ACL also follows whatever the parent says, which is not this application's
/// decision to rely on.
/// </para>
/// <para>
/// What the store holds is worth this: the automations themselves — which run on a keypress — the
/// approval records that decide whether they are trusted, and logs carrying window titles and file
/// paths. Another standard user on a shared machine writing into the automations folder is the
/// threat that matters, and it is exactly what an explicit DACL prevents.
/// </para>
/// <para>
/// Administrators are not in the DACL. That is not a claim to resist them: an administrator can
/// take ownership and rewrite it, and nothing at this level changes that. It removes casual access,
/// which is the honest extent of what a file ACL can offer.
/// </para>
/// </remarks>
public static class StoreAcl
{
    /// <summary>
    /// Create the store root if it is missing, with a DACL granting only this user and SYSTEM.
    /// </summary>
    /// <remarks>
    /// Only on creation. Rewriting the ACL of a directory that already exists would mean deciding,
    /// on someone else's machine, that whatever they or their IT department configured is wrong —
    /// and getting it wrong locks a user out of their own automations. New installs get the
    /// explicit grant; existing ones get <see cref="Audit()"/>, which reports rather than acts.
    /// </remarks>
    public static void EnsureRoot()
    {
        if (Directory.Exists(AgentPaths.Root))
        {
            return;
        }

        try
        {
            new DirectoryInfo(AgentPaths.Root).Create(Desired());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A store the agent cannot protect is still a store it needs. Falling back to a plain
            // create keeps the app working on a machine whose policy forbids setting a DACL, and
            // Audit is what tells the user the control is not in force.
            Directory.CreateDirectory(AgentPaths.Root);
        }
    }

    /// <summary>
    /// Principals that can reach the store but should not, or null if it cannot be read.
    /// </summary>
    /// <remarks>
    /// Reported, never enforced. This is the "assert" half of the control: the agent logs it at
    /// startup and the CLI shows it, so "the store is per-user" is something someone can check
    /// rather than something the design merely intends.
    /// </remarks>
    public static IReadOnlyList<string>? Audit() => Audit(AgentPaths.Root);

    /// <summary>
    /// <see cref="Audit()"/> against a named directory, so the rule can be tested against a folder
    /// a test controls rather than the user's real store.
    /// </summary>
    internal static IReadOnlyList<string>? Audit(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var rules = new DirectoryInfo(directory)
                .GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, true, typeof(SecurityIdentifier));

            var own = WindowsIdentity.GetCurrent().User?.Value ?? "";

            return
            [
                .. rules
                    .Cast<FileSystemAccessRule>()
                    .Where(r => r.AccessControlType == AccessControlType.Allow)
                    .Select(r => r.IdentityReference.Value)
                    .Where(sid => !IsExpected(sid, own))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(Name)
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or PlatformNotSupportedException
                                      or IOException)
        {
            // Cannot tell. Null rather than an empty list, because "nobody unexpected" and "I could
            // not look" must not read the same to whoever is checking.
            return null;
        }
    }

    /// <summary>
    /// Whether a principal granted access to the store is one that should have it.
    /// </summary>
    /// <param name="sid">The SID the rule names.</param>
    /// <param name="ownSid">This user's SID.</param>
    /// <remarks>
    /// SYSTEM is expected because servicing, backup and Defender all run as it, and excluding it
    /// causes failures that look like nothing to do with an ACL. Administrators are expected in the
    /// sense of "not worth reporting" — they can take ownership regardless, so listing them as a
    /// finding would be noise on every machine.
    /// </remarks>
    internal static bool IsExpected(string sid, string ownSid) =>
        string.Equals(sid, ownSid, StringComparison.OrdinalIgnoreCase)
        || sid is "S-1-5-18"      // LOCAL SYSTEM
                or "S-1-5-32-544" // BUILTIN\Administrators
                or "S-1-3-0"      // CREATOR OWNER, which resolves to the owner
                or "S-1-3-4";     // OWNER RIGHTS

    /// <summary>The DACL a fresh store gets: this user and SYSTEM, nothing inherited.</summary>
    private static DirectorySecurity Desired()
    {
        var security = new DirectorySecurity();

        // The load-bearing call. Without it the rules below are added *on top of* whatever
        // %LOCALAPPDATA% grants, and the control remains as implicit as it was.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        if (WindowsIdentity.GetCurrent().User is { } me)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                me, FileSystemRights.FullControl, inherit, PropagationFlags.None,
                AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None,
            AccessControlType.Allow));

        return security;
    }

    private static string Name(string sid)
    {
        try
        {
            return $"{new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value} ({sid})";
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException)
        {
            // An account from a domain this machine cannot reach, or one that no longer exists.
            // The SID alone is still actionable.
            return sid;
        }
    }
}

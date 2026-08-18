using System.Security.AccessControl;
using System.Security.Principal;
using HotkeyAI.Windows;

namespace HotkeyAI.Windows.Tests;

/// <summary>
/// The per-user ACL on the store is set explicitly and can be checked afterwards.
/// </summary>
/// <remarks>
/// PLAN.md control 4 specifies a per-user ACL. <c>%LOCALAPPDATA%</c> inherits a reasonable one —
/// confirmed on this machine: SYSTEM, Administrators and the user, nothing else — so the control
/// held in practice. But nothing set it and nothing asserted it, and a control that is true by
/// accident is one nobody notices becoming false.
/// <para>
/// These run against a temporary directory rather than the real store, which is why
/// <c>Audit</c> takes a path internally.
/// </para>
/// </remarks>
public sealed class StoreAclTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "hotkeyai-acl-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that will not delete is the operating system's problem, not a test
            // failure.
        }
    }

    private static SecurityIdentifier Well(WellKnownSidType type) => new(type, null);

    private void CreateGranting(params SecurityIdentifier[] extras)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, inherit,
            PropagationFlags.None, AccessControlType.Allow));

        foreach (var extra in extras)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                extra, FileSystemRights.Modify, inherit, PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(directory).Create(security);
    }

    // ------------------------------- the audit -------------------------------

    [Fact]
    public void AFolderOnlyThisUserCanReachIsClean()
    {
        CreateGranting();

        Assert.Empty(StoreAcl.Audit(directory)!);
    }

    [Fact]
    public void SystemAndAdministratorsAreNotReported()
    {
        // SYSTEM because servicing, backup and Defender run as it. Administrators because they can
        // take ownership regardless, so reporting them would be noise on every machine — which is
        // how a warning becomes something people scroll past.
        CreateGranting(
            Well(WellKnownSidType.LocalSystemSid),
            Well(WellKnownSidType.BuiltinAdministratorsSid));

        Assert.Empty(StoreAcl.Audit(directory)!);
    }

    [Fact]
    public void EveryoneIsReported()
    {
        // The finding that matters: automations in this folder run on a keypress, so write access
        // is the ability to change what the user's own hotkeys do.
        CreateGranting(Well(WellKnownSidType.WorldSid));

        var unexpected = Assert.Single(StoreAcl.Audit(directory)!);
        Assert.Contains("S-1-1-0", unexpected, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedUsersIsReported()
    {
        // Broad enough to include every other account on a shared machine, which is exactly the
        // threat a per-user ACL is for.
        CreateGranting(Well(WellKnownSidType.AuthenticatedUserSid));

        Assert.NotEmpty(StoreAcl.Audit(directory)!);
    }

    [Fact]
    public void TheReportNamesTheAccountNotJustTheSid()
    {
        CreateGranting(Well(WellKnownSidType.WorldSid));

        var unexpected = Assert.Single(StoreAcl.Audit(directory)!);

        // "Everyone (S-1-1-0)" is actionable; a bare SID makes the reader go and look it up.
        Assert.Contains("(", unexpected, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFolderIsCleanRatherThanUnknown()
    {
        // Nothing to grant access to yet. Distinct from "could not look", which returns null.
        Assert.Empty(StoreAcl.Audit(Path.Combine(directory, "not-created"))!);
    }

    // ------------------------------- the rule -------------------------------

    [Fact]
    public void TheOwnersOwnSidIsExpected()
    {
        Assert.True(StoreAcl.IsExpected("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001"));
    }

    [Fact]
    public void AnotherUsersSidIsNot()
    {
        Assert.False(StoreAcl.IsExpected("S-1-5-21-1-2-3-1002", "S-1-5-21-1-2-3-1001"));
    }

    [Theory]
    [InlineData("S-1-5-18")]        // LOCAL SYSTEM
    [InlineData("S-1-5-32-544")]    // BUILTIN\Administrators
    [InlineData("S-1-3-0")]         // CREATOR OWNER
    public void TheWellKnownExemptionsAreExpected(string sid)
    {
        Assert.True(StoreAcl.IsExpected(sid, "S-1-5-21-1-2-3-1001"));
    }

    [Theory]
    [InlineData("S-1-1-0")]         // Everyone
    [InlineData("S-1-5-11")]        // Authenticated Users
    [InlineData("S-1-5-32-545")]    // BUILTIN\Users
    [InlineData("S-1-5-7")]         // ANONYMOUS LOGON
    public void BroadPrincipalsAreNot(string sid)
    {
        Assert.False(StoreAcl.IsExpected(sid, "S-1-5-21-1-2-3-1001"));
    }
}

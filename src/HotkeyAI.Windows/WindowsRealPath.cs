
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Windows;

/// <summary>
/// Asks the kernel where a path actually leads.
/// </summary>
/// <remarks>
/// The path guard's containment check is a
/// string comparison — correct for <c>..</c>, and what lets it be tested on Linux — but a
/// directory junction created inside the allowed root reaches anywhere on the machine while every
/// path through it still reads as being under the root. Creating one needs no elevation.
/// <para>
/// <c>GetFinalPathNameByHandle</c> rather than <c>File.ResolveLinkTarget</c>, and the difference
/// matters: <c>ResolveLinkTarget</c> resolves the item you name, so it returns null for
/// <c>…\junction\cmd.exe</c> — the file is not a link, the directory above it is. Walking every
/// ancestor by hand would be the alternative; opening a handle and asking the kernel for the
/// canonical name resolves the whole chain in one call and matches what the filesystem will
/// actually do when the path is used.
/// </para>
/// </remarks>
public sealed class WindowsRealPath : IRealPath
{
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;      // read | write | delete
    private const uint NormalisedDosName = 0x0;

    /// <summary>The Win32 device prefix a canonical name comes back with.</summary>
    private const string ExtendedPrefix = @"\\?\";

    public string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // No access rights requested: this only asks the kernel to name the object, and asking for
        // read would fail on files another process holds exclusively — turning "I cannot tell" into
        // a refusal for paths that are perfectly fine.
        var handle = Native.CreateFile(
            path, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);

        if (handle == -1 || handle == 0)
        {
            // Does not exist, or cannot be opened. The guard treats null as no opinion and keeps
            // its lexical verdict, which is right: a path that is not there cannot be a link to
            // somewhere it should not go, and refusing it here would break every plan that names
            // a file it is about to create.
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var length = Fill(handle, buffer);

            // A length larger than the buffer is the API asking for more room, not a failure.
            if (length > buffer.Length)
            {
                buffer = new char[length + 1];
                length = Fill(handle, buffer);
            }

            if (length == 0 || length > buffer.Length)
            {
                return null;
            }

            var resolved = new string(buffer, 0, (int)length);

            // Handed back as \\?\C:\… . Stripped so the value compares against the configured
            // roots, which are ordinary paths — and so a refusal names something the user
            // recognises rather than a device path.
            return resolved.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
                ? resolved[ExtendedPrefix.Length..]
                : resolved;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Hands the kernel a pointer to the buffer's first UTF-16 code unit.</summary>
    private static uint Fill(nint handle, char[] buffer)
    {
        var units = System.Runtime.InteropServices.MemoryMarshal.Cast<char, ushort>(buffer);

        return Native.GetFinalPathNameByHandle(
            handle,
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(units),
            (uint)buffer.Length,
            NormalisedDosName);
    }
}

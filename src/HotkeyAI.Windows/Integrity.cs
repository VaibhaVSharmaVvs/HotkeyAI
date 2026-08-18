using System.Runtime.InteropServices;

namespace HotkeyAI.Windows;

/// <summary>
/// Process integrity level, used to detect windows synthetic input cannot reach.
/// </summary>
/// <remarks>
/// Verified by the Phase 0 spike, which is the reason it has two paths rather than one. For a
/// process we can open, the token carries the integrity RID. For one we cannot, being refused is
/// itself the answer: an unelevated process cannot open a higher-integrity one, so
/// <c>ERROR_ACCESS_DENIED</c> means "higher than us" and nothing else needs to be asked.
/// The spike confirmed this against <c>wininit</c>, <c>services</c>, <c>lsass</c>, <c>csrss</c>,
/// <c>winlogon</c> and <c>smss</c>, while an ordinary process read cleanly.
/// </remarks>
internal static class Integrity
{
    private const int High = 0x3000;

    /// <summary>A SID may carry at most fifteen sub-authorities. Win32's own limit.</summary>
    private const byte MaxSubAuthorities = 15;

    /// <summary>
    /// Byte offset of a SID's last sub-authority, or -1 if the count makes no sense.
    /// </summary>
    /// <remarks>
    /// This used to be inline and unguarded, so a count of zero
    /// computed <c>8 + (-1 * 4) = 4</c> and read four bytes out of the middle of the SID's own
    /// header — an identifier-authority fragment, returned as though it were an integrity level. A
    /// count above fifteen read past the end of the structure entirely.
    /// <para>
    /// Neither can happen for a SID the kernel wrote, which is why it survived: reaching it means
    /// memory that is already wrong. The value it produced was the problem — a plausible-looking
    /// integer that <c>IsHigherThanUs</c> would compare against <c>High</c> and answer confidently.
    /// -1 makes the caller say "unknown" instead.
    /// </para>
    /// <para>
    /// Unknown resolves to "not higher than us", which is this file's existing posture for a failed
    /// read: access denied is evidence of elevation and nothing else is. Stated rather than
    /// assumed, because it is a fail-open choice — but the alternative refuses input to any window
    /// whose token cannot be classified, which on a normal desktop is a great many of them.
    /// </para>
    /// </remarks>
    internal static int SubAuthorityOffset(byte subAuthorities) =>
        subAuthorities is 0 or > MaxSubAuthorities
            ? -1
            : 8 + ((subAuthorities - 1) * 4);

    /// <summary>True if the process runs at a higher integrity level than this one.</summary>
    internal static bool IsHigherThanUs(uint processId)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (process == 0)
        {
            // The denial is the signal. Any other failure (the process exited, say) is not
            // evidence of elevation, so do not report it as such.
            return Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED;
        }

        try
        {
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token))
            {
                return Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED;
            }

            try
            {
                return LevelOf(token) >= High;
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static int LevelOf(nint token)
    {
        Native.GetTokenInformation(token, Native.TokenIntegrityLevel, 0, 0, out var required);
        if (required <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!Native.GetTokenInformation(
                    token, Native.TokenIntegrityLevel, buffer, required, out required))
            {
                return 0;
            }

            // TOKEN_MANDATORY_LABEL is a SID_AND_ATTRIBUTES whose first field is the SID; the
            // integrity level is that SID's last sub-authority.
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == 0)
            {
                return 0;
            }

            var offset = SubAuthorityOffset(Marshal.ReadByte(sid, 1));
            return offset < 0 ? 0 : Marshal.ReadInt32(sid, offset);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

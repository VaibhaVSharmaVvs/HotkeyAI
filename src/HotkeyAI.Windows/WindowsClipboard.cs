using System.Runtime.InteropServices;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Windows;

/// <summary>
/// Clipboard access via raw Win32.
/// </summary>
/// <remarks>
/// <para>
/// Raw P/Invoke rather than WinForms or WPF, so this library stays free of a UI framework: the
/// CLI runs automations without one, and pulling in WPF to read a string would make a headless
/// run impossible.
/// </para>
/// <para>
/// The clipboard is a shared, contended resource — another application can hold it open — so
/// every operation retries briefly rather than failing on the first refusal, and always closes
/// what it opened.
/// </para>
/// </remarks>
public sealed class WindowsClipboard : IClipboard
{
    private const int Attempts = 10;
    private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(30);

    public async ValueTask<string> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await TryOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            return "";
        }

        try
        {
            if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT))
            {
                // An image or a file list on the clipboard is not an error; there is simply no
                // text to read.
                return "";
            }

            var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (handle == 0)
            {
                return "";
            }

            var pointer = Native.GlobalLock(handle);
            if (pointer == 0)
            {
                return "";
            }

            try
            {
                return Marshal.PtrToStringUni(pointer) ?? "";
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!await TryOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Another application is holding the clipboard open.");
        }

        try
        {
            Native.EmptyClipboard();

            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var memory = Native.GlobalAlloc(Native.GMEM_MOVEABLE, bytes);
            if (memory == 0)
            {
                throw new InvalidOperationException("Could not allocate clipboard memory.");
            }

            var pointer = Native.GlobalLock(memory);
            if (pointer == 0)
            {
                throw new InvalidOperationException("Could not lock clipboard memory.");
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                Native.GlobalUnlock(memory);
            }

            // Ownership of the block transfers to the clipboard on success; freeing it here
            // would corrupt what the next reader sees.
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, memory) == 0)
            {
                throw new InvalidOperationException("The clipboard rejected the data.");
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static async ValueTask<bool> TryOpenAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Native.OpenClipboard(0))
            {
                return true;
            }

            await Task.Delay(Backoff, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}

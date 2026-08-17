using System.Runtime.InteropServices;
using System.Text;

namespace HotkeyAI.Windows;

/// <summary>P/Invoke surface. Confined to this project so nothing above it sees Win32.</summary>
internal static partial class Native
{
    internal const int SW_MINIMIZE = 6;
    internal const int SW_MAXIMIZE = 3;
    internal const int SW_RESTORE = 9;
    internal const uint WM_CLOSE = 0x0010;

    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint INPUT_KEYBOARD = 1;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenIntegrityLevel = 25;
    internal const int ERROR_ACCESS_DENIED = 5;

    internal const uint CF_UNICODETEXT = 13;
    internal const uint GMEM_MOVEABLE = 0x0002;

    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowText(nint window, [Out] char[] text, int max);

    // EntryPoint is mandatory for every A/W pair. LibraryImport is exact-spelling always,
    // unlike DllImport, which defaulted to trying the "W" suffix — so a missing EntryPoint here
    // compiles cleanly and then throws EntryPointNotFoundException the first time it runs.
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(nint window, [Out] char[] text, int max);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    /// <summary>Which control has the keyboard focus, among other UI state of one thread.</summary>
    /// <remarks>
    /// The foreground window is not what receives typing — the focused child control is, and only
    /// this call names it. Security review 2026-08-17, finding M6, needs it to see whether that
    /// control carries the password style.
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    /// <summary>
    /// State of one thread's UI. <c>Size</c> must be set before the call or it fails.
    /// </summary>
    /// <remarks>
    /// Declared in full rather than trimmed to the one field needed, because the API validates
    /// <c>cbSize</c> against the real structure and a short one is simply refused.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rect CaretRect;
    }

    internal const int GWL_STYLE = -16;

    /// <summary>Edit control style: the text is masked.</summary>
    internal const int ES_PASSWORD = 0x0020;

    /// <remarks>
    /// The 64-bit entry point, and the only one that exists in a 64-bit process — <c>GetWindowLongW</c>
    /// is present too but truncates. The style bits fit in 32 bits either way; this is about being
    /// spelled correctly rather than about the width.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(
        uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveWindow(
        nint window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        nint dc, nint clip, MonitorEnumProc callback, nint data);

    internal delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    internal const uint MONITORINFOF_PRIMARY = 1;

    // ------------------------------- input -------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        // Padding so the union matches the largest member, MOUSEINPUT, which is 32 bytes on
        // x64: two LONGs, three DWORDs, four bytes of alignment, then a ULONG_PTR. Getting this
        // wrong is invisible rather than fatal — SendInput compares its cbSize argument against
        // its own idea of the struct, and on a mismatch it sends nothing, returns 0 and sets no
        // useful error. KEYBDINPUT is only 24 bytes, so a union sized to it looks perfectly
        // reasonable and silently disables every keystroke the app can send.
        [FieldOffset(0)]
        private readonly long padding0;

        [FieldOffset(8)]
        private readonly long padding1;

        [FieldOffset(16)]
        private readonly long padding2;

        [FieldOffset(24)]
        private readonly long padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint count, Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int key);

    // ------------------------------- real paths -------------------------------

    /// <summary>
    /// Opens a path only to ask the kernel what it is, so the handle wants no access rights.
    /// </summary>
    /// <remarks>
    /// <c>FILE_FLAG_BACKUP_SEMANTICS</c> is what allows a *directory* to be opened this way, which
    /// is required: a junction sits on a directory, and the interesting case is a junction partway
    /// along a path rather than at its end.
    /// </remarks>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFile(
        string path,
        uint access,
        uint share,
        nint security,
        uint disposition,
        uint flags,
        nint template);

    /// <remarks>
    /// The buffer is a pointer to UTF-16 code units, typed <c>ref ushort</c>. Three narrower
    /// spellings all fail: a StringBuilder the source generator cannot marshal at all, a
    /// <c>char[]</c> needs runtime marshalling disabled assembly-wide, and even <c>ref char</c> is
    /// rejected because <c>char</c>'s width is a marshalling decision rather than a fixed size.
    /// <c>ushort</c> is unambiguously two bytes, so nothing has to be decided. The caller wants the
    /// returned length regardless, to know how much of the buffer is real.
    /// </remarks>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(
        nint file, ref ushort path, uint length, uint flags);

    // ------------------------------- integrity -------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        nint token, int informationClass, nint information, int length, out int required);

    // ------------------------------- clipboard -------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetClipboardData(uint format, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(nint handle);

    // ------------------------------- helpers -------------------------------

    internal static string GetWindowTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        var written = GetWindowText(window, buffer, buffer.Length);
        return written <= 0 ? "" : new string(buffer, 0, written);
    }

    internal static string GetWindowClass(nint window)
    {
        var buffer = new char[256];
        var written = GetClassName(window, buffer, buffer.Length);
        return written <= 0 ? "" : new string(buffer, 0, written);
    }

    // ------------------------------- hotkeys and the message pump -------------------------------

    internal const uint WM_HOTKEY = 0x0312;
    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_NULL = 0x0000;
    internal const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out Msg message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(
        uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    // ------------------------------- console -------------------------------

    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeConsole();

    internal static StringBuilder Unused { get; } = new();
}

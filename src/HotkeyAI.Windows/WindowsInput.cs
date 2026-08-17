using System.ComponentModel;
using System.Runtime.InteropServices;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Windows;

/// <summary>Synthetic keyboard input, and the checks that decide whether to send any.</summary>
public sealed class WindowsInput : IInput
{
    /// <summary>Window classes that mean a credential or consent prompt has focus.</summary>
    /// <remarks>
    /// Safety control 3. UAC consent runs on a separate secure desktop, so an unelevated process
    /// usually cannot even see it — <see cref="CheckHazardAsync"/> treats an unreadable
    /// foreground window as a hazard for exactly that reason.
    /// <para>
    /// <c>#32770</c> was on this list and has been removed. It is the class of every standard
    /// Win32 dialog — Run, Save As, Find, most installers — not of credential prompts
    /// specifically, so it refused input to ordinary dialogs and told the user a password field
    /// had focus, which was simply untrue. Found by typing into the Run dialog. A guard that
    /// fires on the common case teaches people to distrust it, which costs more safety than the
    /// rare credential dialog it caught. The specific credential classes below, plus the
    /// integrity check in <see cref="CheckHazardAsync"/>, are what actually carry this control.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> CredentialClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Credential Dialog Xaml Host",
            "ConsentUI",
        };

    private static readonly Dictionary<AppCommand, ushort> MediaKeys = new()
    {
        [AppCommand.MediaPlayPause] = 0xB3,
        [AppCommand.MediaNextTrack] = 0xB0,
        [AppCommand.MediaPreviousTrack] = 0xB1,
        [AppCommand.MediaStop] = 0xB2,
        [AppCommand.VolumeUp] = 0xAF,
        [AppCommand.VolumeDown] = 0xAE,
        [AppCommand.VolumeMute] = 0xAD,
        [AppCommand.BrowserBack] = 0xA6,
        [AppCommand.BrowserForward] = 0xA7,
        [AppCommand.BrowserRefresh] = 0xA8,
    };

    private static readonly Dictionary<KeyName, ushort> VirtualKeys = BuildKeyMap();

    /// <summary>Delay between typed characters. See <see cref="TypeTextAsync"/> for why.</summary>
    private const int TypingIntervalMs = 5;

    public ValueTask<InputHazard> CheckHazardAsync(CancellationToken cancellationToken)
    {
        var window = Native.GetForegroundWindow();

        // No foreground window at all: nothing would receive the input, so refuse rather than
        // fire keystrokes into the void.
        if (window == 0)
        {
            return ValueTask.FromResult(InputHazard.ConsentPrompt);
        }

        var className = Native.GetWindowClass(window);
        if (CredentialClasses.Contains(className))
        {
            return ValueTask.FromResult(InputHazard.CredentialPrompt);
        }

        var thread = Native.GetWindowThreadProcessId(window, out var processId);

        if (Integrity.IsHigherThanUs(processId))
        {
            // The important one. Windows discards synthetic input aimed at a higher-integrity
            // window and reports nothing, so without this check the automation appears to
            // succeed while doing absolutely nothing.
            return ValueTask.FromResult(InputHazard.ElevatedWindow);
        }

        if (FocusIsMasked(thread))
        {
            return ValueTask.FromResult(InputHazard.CredentialPrompt);
        }

        return ValueTask.FromResult(InputHazard.None);
    }

    /// <summary>Edit-control classes whose password style is worth reading.</summary>
    /// <remarks>
    /// The style bit is only <c>ES_PASSWORD</c> on an edit control; <c>0x0020</c> means something
    /// else entirely on a button or a list box, so the class has to be established first or the
    /// check invents password fields where there are none.
    /// </remarks>
    private static readonly HashSet<string> EditClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Edit", "RichEdit", "RichEdit20A", "RichEdit20W", "RichEdit50W", "RICHEDIT60W",
        };

    /// <summary>
    /// Whether a window class is an edit control, superclassed or not.
    /// </summary>
    /// <remarks>
    /// The second half matters more than the first. A WinForms <c>TextBox</c> is a real edit control
    /// with the real <c>ES_PASSWORD</c> style, but WinForms superclasses it and the class name comes
    /// back as <c>WindowsForms10.EDIT.app.0.1405e41_r25_ad1</c> — so an exact-match list finds a
    /// plain Win32 dialog's password box and misses every managed one, which is most of them. This
    /// was found by probing a live masked TextBox rather than by reading, and it is the reason the
    /// probe was worth writing.
    /// </remarks>
    internal static bool IsEditControl(string className) =>
        EditClasses.Contains(className)
        || (className.StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)
            && className.Split('.') is [_, var control, ..]
            && EditClasses.Contains(control));

    /// <summary>
    /// Whether the focused control masks what is typed into it.
    /// </summary>
    /// <remarks>
    /// Security review 2026-08-17, finding M6. PLAN.md control 3 claimed a password-style check that
    /// did not exist: the code tested two window class names and the integrity level, so the
    /// foreground being a credential *dialog* was caught while a password *field* inside an ordinary
    /// window was not.
    /// <para>
    /// The foreground window is not what receives typing — the focused child control is — so this
    /// asks the foreground thread which control has the focus and reads its style. That covers Win32
    /// and WinForms. It does not cover WPF, Chromium or Electron, where the focused element is not a
    /// window at all and only UI Automation can see it; PLAN.md control 3 now says so rather than
    /// implying otherwise, because a control described more broadly than it is implemented is worse
    /// than a narrow one honestly described.
    /// </para>
    /// </remarks>
    private static bool FocusIsMasked(uint foregroundThread)
    {
        var info = new Native.GuiThreadInfo();
        info.Size = Marshal.SizeOf<Native.GuiThreadInfo>();

        if (!Native.GetGUIThreadInfo(foregroundThread, ref info) || info.Focus == 0)
        {
            // No focused control, or a thread that will not answer — which is the ordinary case for
            // a Chromium window. Not a hazard on its own: reporting one here would refuse input to
            // every browser.
            return false;
        }

        if (!IsEditControl(Native.GetWindowClass(info.Focus)))
        {
            return false;
        }

        return ((int)Native.GetWindowLongPtr(info.Focus, Native.GWL_STYLE) & Native.ES_PASSWORD) != 0;
    }

    public ValueTask SendChordAsync(
        IReadOnlyList<KeyName> keys, int repeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var modifiers = keys.Where(Keys.IsModifier).Select(Code).ToList();
        var main = keys.Where(k => !Keys.IsModifier(k)).Select(Code).ToList();

        for (var i = 0; i < Math.Max(1, repeat); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sequence = new List<Native.Input>();

            foreach (var modifier in modifiers)
            {
                sequence.Add(Key(modifier, down: true));
            }

            foreach (var key in main)
            {
                sequence.Add(Key(key, down: true));
                sequence.Add(Key(key, down: false));
            }

            // Released in reverse, mirroring how a person lets go of a chord.
            for (var m = modifiers.Count - 1; m >= 0; m--)
            {
                sequence.Add(Key(modifiers[m], down: false));
            }

            Send(sequence);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Unicode scan codes rather than virtual keys, so the text arrives as written whatever
        // keyboard layout is active. A plan typing "£" must not depend on the user's layout.
        //
        // Typed one character at a time, paced. Sending the whole string as a single SendInput
        // batch delivers corrupted text: measured against Windows 11 Notepad, "HotkeyAI probe OK"
        // arrived as "HotkeyAI KKKKKKKK" and "git checkout -b feature/my-branch" as
        // "git kkkkkout hhhhhhhhhhhhhhhhhhhh". Runs of characters collapse onto a later character
        // of the run, and the result varies between identical runs, so this is a race in the
        // target's input processing rather than anything wrong with the events themselves.
        //
        // Both halves are load-bearing and each was tested alone: one batch corrupts, and
        // per-character calls with no delay corrupt too. Only pacing them fixes it. The cost is
        // 5 ms per character — a 60-character string takes a third of a second, which is
        // invisible next to the application launches these plans usually wait on.
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send([Unicode(character, down: true), Unicode(character, down: false)]);
            Thread.Sleep(TypingIntervalMs);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendAppCommandAsync(AppCommand command, CancellationToken cancellationToken)
    {
        if (!MediaKeys.TryGetValue(command, out var key))
        {
            return ValueTask.CompletedTask;
        }

        // A media key press rather than a WM_APPCOMMAND broadcast: the shell routes it to
        // whichever application currently owns playback, which is the whole point of the
        // primitive — no window to find, no player to name.
        Send([Key(key, down: true), Key(key, down: false)]);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseModifiersAsync()
    {
        // The panic path. Only release what is actually held, so this cannot itself generate
        // spurious key-up events, and never throw — this runs while aborting.
        try
        {
            var held = new List<Native.Input>();

            foreach (var modifier in new ushort[] { 0x11, 0x12, 0x10, 0x5B, 0x5C })
            {
                if ((Native.GetAsyncKeyState(modifier) & 0x8000) != 0)
                {
                    held.Add(Key(modifier, down: false));
                }
            }

            if (held.Count > 0)
            {
                Send(held);
            }
        }
#pragma warning disable CA1031 // Best effort on the abort path.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------------------

    private static void Send(List<Native.Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var array = inputs.ToArray();
        var sent = Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Native.Input>());

        // Never ignore this. SendInput reports how many events it injected, and a short count is
        // the only signal that anything went wrong — the call does not throw, and the engine
        // would otherwise log "Sent Ctrl+C" for a keystroke that was never delivered. A wrong
        // INPUT size, a UIPI block from a higher-integrity foreground window, and the low-level
        // hook chain rejecting the batch all present identically as a silent short count.
        if (sent != array.Length)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"The system accepted {sent} of {array.Length} input events. This usually means "
                + "the foreground window is running at a higher integrity level and is rejecting "
                + "synthetic input, which Windows does not otherwise report.");
        }
    }

    private static Native.Input Key(ushort virtualKey, bool down) => new()
    {
        Type = Native.INPUT_KEYBOARD,
        Union = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = down ? 0 : Native.KEYEVENTF_KEYUP,
            },
        },
    };

    private static Native.Input Unicode(char character, bool down) => new()
    {
        Type = Native.INPUT_KEYBOARD,
        Union = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput
            {
                ScanCode = character,
                Flags = Native.KEYEVENTF_UNICODE | (down ? 0 : Native.KEYEVENTF_KEYUP),
            },
        },
    };

    private static ushort Code(KeyName key) =>
        VirtualKeys.TryGetValue(key, out var code) ? code : (ushort)0;

    private static Dictionary<KeyName, ushort> BuildKeyMap()
    {
        var map = new Dictionary<KeyName, ushort>
        {
            [KeyName.Ctrl] = 0x11,
            [KeyName.Alt] = 0x12,
            [KeyName.Shift] = 0x10,
            [KeyName.Win] = 0x5B,
            [KeyName.Space] = 0x20,
            [KeyName.Enter] = 0x0D,
            [KeyName.Tab] = 0x09,
            [KeyName.Esc] = 0x1B,
            [KeyName.Backspace] = 0x08,
            [KeyName.Delete] = 0x2E,
            [KeyName.Insert] = 0x2D,
            [KeyName.Home] = 0x24,
            [KeyName.End] = 0x23,
            [KeyName.PageUp] = 0x21,
            [KeyName.PageDown] = 0x22,
            [KeyName.Left] = 0x25,
            [KeyName.Right] = 0x27,
            [KeyName.Up] = 0x26,
            [KeyName.Down] = 0x28,
            [KeyName.OemComma] = 0xBC,
            [KeyName.OemPeriod] = 0xBE,
            [KeyName.OemMinus] = 0xBD,
            [KeyName.OemPlus] = 0xBB,
            [KeyName.Oem1] = 0xBA,
            [KeyName.Oem2] = 0xBF,
            [KeyName.Oem3] = 0xC0,
            [KeyName.Oem4] = 0xDB,
            [KeyName.Oem5] = 0xDC,
            [KeyName.Oem6] = 0xDD,
            [KeyName.Oem7] = 0xDE,
        };

        // A–Z and 0–9 map to their ASCII codes, which is what the Win32 virtual-key table uses.
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            map[Enum.Parse<KeyName>(letter.ToString())] = letter;
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            map[Enum.Parse<KeyName>($"D{digit}")] = (ushort)('0' + digit);
        }

        for (var function = 1; function <= 12; function++)
        {
            map[Enum.Parse<KeyName>($"F{function}")] = (ushort)(0x70 + function - 1);
        }

        return map;
    }
}

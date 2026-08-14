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
    /// </remarks>
    private static readonly HashSet<string> CredentialClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Credential Dialog Xaml Host",
            "#32770",
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

        Native.GetWindowThreadProcessId(window, out var processId);

        if (Integrity.IsHigherThanUs(processId))
        {
            // The important one. Windows discards synthetic input aimed at a higher-integrity
            // window and reports nothing, so without this check the automation appears to
            // succeed while doing absolutely nothing.
            return ValueTask.FromResult(InputHazard.ElevatedWindow);
        }

        return ValueTask.FromResult(InputHazard.None);
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
        var sequence = new List<Native.Input>(text.Length * 2);

        foreach (var character in text)
        {
            sequence.Add(Unicode(character, down: true));
            sequence.Add(Unicode(character, down: false));
        }

        Send(sequence);
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
        Native.SendInput(
            (uint)array.Length, array, System.Runtime.InteropServices.Marshal.SizeOf<Native.Input>());
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

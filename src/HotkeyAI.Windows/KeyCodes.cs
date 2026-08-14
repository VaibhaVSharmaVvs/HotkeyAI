using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Windows;

/// <summary>DSL key names to Win32 virtual-key codes and hotkey modifier bits.</summary>
public static class KeyCodes
{
    /// <summary>Modifier bits for <c>RegisterHotKey</c>.</summary>
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// Suppresses auto-repeat while the chord is held.
    /// </summary>
    /// <remarks>
    /// Confirmed supported by the Phase 0 spike. Without it, holding the chord fires the
    /// automation once per repeat — which for a plan that launches an application means a
    /// screenful of windows from one long keypress.
    /// </remarks>
    public const uint ModNoRepeat = 0x4000;

    private static readonly Dictionary<KeyName, ushort> VirtualKeys = Build();

    /// <summary>The virtual-key code for a key name.</summary>
    public static ushort VirtualKey(KeyName key) =>
        VirtualKeys.TryGetValue(key, out var code) ? code : (ushort)0;

    /// <summary>Split a chord into modifier bits and its single non-modifier key.</summary>
    /// <returns>False if the chord has no usable non-modifier key.</returns>
    public static bool TrySplit(IReadOnlyList<KeyName> chord, out uint modifiers, out ushort key)
    {
        ArgumentNullException.ThrowIfNull(chord);

        modifiers = 0;
        key = 0;

        foreach (var part in chord)
        {
            switch (part)
            {
                case KeyName.Ctrl: modifiers |= ModControl; break;
                case KeyName.Alt: modifiers |= ModAlt; break;
                case KeyName.Shift: modifiers |= ModShift; break;
                case KeyName.Win: modifiers |= ModWin; break;
                default: key = VirtualKey(part); break;
            }
        }

        return key != 0;
    }

    private static Dictionary<KeyName, ushort> Build()
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

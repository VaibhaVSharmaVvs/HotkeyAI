using System.Windows.Input;
using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Ui;

/// <summary>
/// Turns a WPF key event into the DSL's key names.
/// </summary>
/// <remarks>
/// Only the keys the DSL actually has. An unmapped key returns null rather than guessing, so a
/// combination the schema could not express is refused at the moment it is pressed instead of
/// being written into a plan that then fails validation.
/// </remarks>
internal static class KeyCapture
{
    private static readonly Dictionary<Key, KeyName> Keys = Build();

    /// <summary>
    /// Read the chord from a key event, or null if it is not one yet.
    /// </summary>
    /// <remarks>
    /// Returns null while only modifiers are held. That is the normal state halfway through
    /// pressing a combination, not an error — the caller keeps waiting rather than complaining.
    /// </remarks>
    public static IReadOnlyList<KeyName>? Read(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // With Alt held, WPF reports Key.System and puts the real key in SystemKey. Without this
        // every Alt combination captures as the literal key "System", which is not in the DSL, so
        // Alt chords would appear unbindable.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (!Keys.TryGetValue(key, out var main))
        {
            return null;
        }

        var chord = new List<KeyName>();
        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            chord.Add(KeyName.Ctrl);
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            chord.Add(KeyName.Alt);
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            chord.Add(KeyName.Shift);
        }

        // WPF does not report Windows as a modifier, because the shell swallows most of those
        // combinations before any application sees them. Reading the key state directly is the
        // only way to know, and a chord including Win is still worth offering — the OS will
        // refuse the reserved ones at registration, which the probe reports honestly.
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            chord.Add(KeyName.Win);
        }

        chord.Add(main);
        return HotkeyChord.Normalise(chord);
    }

    /// <summary>Whether this key press is the user backing out rather than choosing.</summary>
    /// <remarks>
    /// Escape on its own cancels. Escape with a modifier is a perfectly good chord, so the two
    /// have to be told apart or Ctrl+Alt+Esc could never be bound.
    /// </remarks>
    public static bool IsCancel(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None
            && !Keyboard.IsKeyDown(Key.LWin) && !Keyboard.IsKeyDown(Key.RWin);
    }

    private static Dictionary<Key, KeyName> Build()
    {
        var map = new Dictionary<Key, KeyName>
        {
            [Key.Space] = KeyName.Space,
            [Key.Enter] = KeyName.Enter,
            [Key.Tab] = KeyName.Tab,
            [Key.Escape] = KeyName.Esc,
            [Key.Back] = KeyName.Backspace,
            [Key.Delete] = KeyName.Delete,
            [Key.Insert] = KeyName.Insert,
            [Key.Home] = KeyName.Home,
            [Key.End] = KeyName.End,
            [Key.PageUp] = KeyName.PageUp,
            [Key.PageDown] = KeyName.PageDown,
            [Key.Left] = KeyName.Left,
            [Key.Right] = KeyName.Right,
            [Key.Up] = KeyName.Up,
            [Key.Down] = KeyName.Down,
            [Key.OemComma] = KeyName.OemComma,
            [Key.OemPeriod] = KeyName.OemPeriod,
            [Key.OemMinus] = KeyName.OemMinus,
            [Key.OemPlus] = KeyName.OemPlus,
            [Key.Oem1] = KeyName.Oem1,
            [Key.Oem2] = KeyName.Oem2,
            [Key.Oem3] = KeyName.Oem3,
            [Key.Oem4] = KeyName.Oem4,
            [Key.Oem5] = KeyName.Oem5,
            [Key.Oem6] = KeyName.Oem6,
            [Key.Oem7] = KeyName.Oem7,
        };

        for (var letter = 0; letter < 26; letter++)
        {
            map[Key.A + letter] = KeyName.A + letter;
        }

        for (var digit = 0; digit < 10; digit++)
        {
            map[Key.D0 + digit] = KeyName.D0 + digit;

            // The numeric keypad produces its own virtual keys, but a user pressing 4 on the
            // keypad means the same combination they would describe as "4".
            map[Key.NumPad0 + digit] = KeyName.D0 + digit;
        }

        for (var function = 0; function < 12; function++)
        {
            map[Key.F1 + function] = KeyName.F1 + function;
        }

        return map;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HotkeyAI.Core;
using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Ui;

/// <summary>
/// A field you fill by pressing the combination, not by typing its name.
/// </summary>
/// <remarks>
/// This replaced a text box that asked for <c>CTRL+ALT+J</c> as a string, which was wrong in
/// three separate ways: it made the user spell a chord in a notation they had to guess, it
/// accepted combinations that do not exist, and it could not tell them the chord was already
/// taken until after the plan had been written and refused. Pressing the keys answers all three
/// at once — the notation is generated, an unmappable key never arrives, and availability is
/// checked on the keypress.
/// <para>
/// Every hotkey in the application is released while this listens, which is not optional. Windows
/// delivers a registered chord to the thread that registered it and never to the focused window,
/// so while the agent holds Ctrl+Alt+X, pressing it would run that automation instead of reaching
/// this control — leaving the field blind to precisely the combinations it most needs to warn
/// about. The suspension is scoped tightly: it starts when listening starts and is released on
/// capture, on Escape, and on losing focus, because a dashboard that quietly leaves every hotkey
/// dead is a worse bug than the one this fixes.
/// </para>
/// </remarks>
internal sealed class ChordField : Border
{
    private readonly IDashboardHost host;
    private readonly TextBlock display;
    private readonly TextBlock verdict;
    private readonly Button trigger;

    private IDisposable? suspension;
    private bool listening;

    internal ChordField(IDashboardHost host)
    {
        this.host = host;

        display = new TextBlock
        {
            Text = "None",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var cap = new Border
        {
            Background = Palette.Selection,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 4, 10, 4),
            Child = display,
        };

        trigger = new Button
        {
            Content = "Set combination",
            Foreground = Palette.Accent,
            FontSize = 12.5,
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand,
            Focusable = true,
            Template = Fluent.OutlineButtonTemplate(Palette.Accent),
        };

        trigger.Click += (_, _) => Listen();

        verdict = new TextBlock
        {
            Text = "Optional. Leave it unset and the plan can name one later.",
            Foreground = Palette.Muted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(cap);
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(trigger);

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(verdict);

        Child = stack;
        Background = Brushes.Transparent;

        // On the button, not on this Border.
        //
        // The first version made the Border focusable and moved keyboard focus to it when
        // listening began. A Border is a poor focus host: focus did not settle, LostKeyboardFocus
        // fired immediately, and Release ran before a single key arrived — which presented as a
        // field that said "hold the modifiers and press a key" while having already stopped
        // listening. The button is a real focusable control and already has focus from the click
        // that started this, so nothing needs moving.
        trigger.PreviewKeyDown += OnKey;
        trigger.LostKeyboardFocus += (_, _) => Release();
    }

    /// <summary>The captured combination, or null if the user has not set one.</summary>
    internal IReadOnlyList<KeyName>? Chord { get; private set; }

    /// <summary>Forget the combination and stop listening. For clearing the form after a save.</summary>
    internal void Reset()
    {
        Release();
        Chord = null;
        display.Text = "None";
        display.Foreground = Palette.Muted;
        verdict.Text = "Optional. Leave it unset and the plan can name one later.";
        verdict.Foreground = Palette.Muted;
    }

    private void Listen()
    {
        if (listening)
        {
            return;
        }

        listening = true;
        suspension = host.SuspendHotkeys();

        trigger.Content = "Listening…";
        display.Text = "Press now";
        display.Foreground = Palette.Accent;
        verdict.Text = "Hold the modifiers and press a key. Esc stops listening.";
        verdict.Foreground = Palette.Muted;

        // Usually already focused, since a click is what got here — but not when the button is
        // invoked through UI Automation, which is also how this gets tested.
        trigger.Focus();
    }

    private void Release()
    {
        if (!listening)
        {
            return;
        }

        listening = false;
        suspension?.Dispose();
        suspension = null;
        trigger.Content = Chord is null ? "Set combination" : "Change";

        if (Chord is null)
        {
            display.Text = "None";
            display.Foreground = Palette.Muted;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!listening)
        {
            return;
        }

        // Always handled while listening. Otherwise Tab moves focus and Space presses the button,
        // and neither can then be captured as part of a combination.
        e.Handled = true;

        if (KeyCapture.IsCancel(e))
        {
            Release();
            return;
        }

        if (KeyCapture.Read(e) is not { } chord)
        {
            // Modifiers only so far. Showing them keeps the field responsive while the user is
            // still reaching for the third key.
            display.Text = Describe(Held());
            display.Foreground = Palette.Accent;
            return;
        }

        display.Text = PlanRenderer.DescribeTrigger(new HotkeyAI.Core.Dsl.Trigger { Keys = chord });

        // Checked here rather than on save. An empty file name matches no automation, so this is
        // the availability question for a combination nothing owns yet — which is exactly the
        // question being asked.
        var availability = host.CheckHotkey("", chord);

        verdict.Text = availability.Message;
        verdict.Foreground = availability.CanBind ? Palette.Good : Palette.Danger;
        display.Foreground = availability.CanBind ? Palette.Text : Palette.Danger;

        // Kept either way. A taken combination is still what the user asked for, and the plan is
        // going to Claude Code before it runs — refusing to even hold it would mean retyping the
        // description to try a different key.
        Chord = chord;
        Release();
    }

    private static List<KeyName> Held()
    {
        var held = new List<KeyName>();

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            held.Add(KeyName.Ctrl);
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            held.Add(KeyName.Alt);
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            held.Add(KeyName.Shift);
        }

        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            held.Add(KeyName.Win);
        }

        return held;
    }

    private static string Describe(List<KeyName> chord) =>
        chord.Count == 0 ? "Press now" : PlanRenderer.DescribeTrigger(new HotkeyAI.Core.Dsl.Trigger { Keys = chord });
}

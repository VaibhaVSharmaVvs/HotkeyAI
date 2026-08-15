using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HotkeyAI.Core;
using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Ui;

/// <summary>
/// Press a combination to rebind an automation.
/// </summary>
/// <remarks>
/// The check runs on every keypress rather than on save, so the user learns that a chord is taken
/// while still holding it — at which point trying another costs nothing. Finding out afterwards
/// means undoing a change you have already committed to a file.
/// </remarks>
internal sealed class HotkeyCaptureWindow : Window
{
    private readonly IDashboardHost host;
    private readonly string fileName;
    private readonly TextBlock chordText;
    private readonly TextBlock verdict;
    private readonly Button save;

    private IReadOnlyList<KeyName>? captured;

    private HotkeyCaptureWindow(IDashboardHost host, string fileName, string name, string current)
    {
        this.host = host;
        this.fileName = fileName;

        Title = $"Hotkey for {name}";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Background = Palette.Surface;
        Foreground = Palette.Text;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(20) };

        layout.Children.Add(new TextBlock
        {
            Text = "Press the combination you want.",
            Foreground = Palette.Muted,
            FontSize = 12,
        });

        chordText = new TextBlock
        {
            Text = current,
            Foreground = Palette.Text,
            FontSize = 22,
            Margin = new Thickness(0, 14, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        layout.Children.Add(chordText);

        verdict = new TextBlock
        {
            Text = "Waiting for a keypress.",
            Foreground = Palette.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };

        layout.Children.Add(verdict);

        layout.Children.Add(new TextBlock
        {
            // Said plainly rather than buried, because it is the one thing this window cannot
            // find out. See the availability caveat in the Phase 0 notes.
            Text = "A combination held by a low-level keyboard hook — some AutoHotkey scripts, "
                 + "push-to-talk — looks free here and then never fires.",
            Foreground = Palette.Muted,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        save = Button("Save", Commit);
        save.IsEnabled = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        buttons.Children.Add(Button("Cancel  (Esc)", Close));
        buttons.Children.Add(save);
        layout.Children.Add(buttons);

        Content = layout;

        PreviewKeyDown += OnKey;

        // Capture only works while this window has the keyboard, and it can lose it without
        // being closed — pressing a shell-reserved combination such as Ctrl+Shift+Esc hands
        // focus elsewhere mid-capture. Left open it would sit there looking ready and quietly
        // recording nothing, so it closes instead of lying about being able to listen.
        Deactivated += (_, _) => Close();
        SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    /// <summary>Show the capture window; returns true if the automation was rebound.</summary>
    internal static bool Show(
        Window owner, IDashboardHost host, string fileName, string name, string current)
    {
        // Every chord is released for as long as this window is open, and restored on the way
        // out. Without it the window never sees a combination that is already bound, which is
        // exactly the case it exists to report on.
        using var suspended = host.SuspendHotkeys();

        var window = new HotkeyCaptureWindow(host, fileName, name, current) { Owner = owner };
        return window.ShowDialog() == true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Always handled. Otherwise Tab moves focus and Space presses the focused button, and
        // neither can then be captured as part of a chord.
        e.Handled = true;

        if (KeyCapture.IsCancel(e))
        {
            Close();
            return;
        }

        if (KeyCapture.Read(e) is not { } chord)
        {
            // Modifiers only so far. Show them so the window feels responsive while the user is
            // still reaching for the third key.
            chordText.Text = Describe(Held());
            verdict.Text = "Now press a key.";
            verdict.Foreground = Palette.Muted;
            save.IsEnabled = false;
            return;
        }

        captured = chord;
        chordText.Text = Describe(chord);

        var availability = host.CheckHotkey(fileName, chord);
        verdict.Text = availability.Message;
        verdict.Foreground = availability.CanBind ? Palette.Muted : Palette.Danger;
        save.IsEnabled = availability.CanBind;
    }

    private void Commit()
    {
        if (captured is null)
        {
            return;
        }

        var error = host.SetHotkey(fileName, captured);

        if (error is not null)
        {
            verdict.Text = error;
            verdict.Foreground = Palette.Danger;
            save.IsEnabled = false;
            return;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>The modifiers currently held, for feedback before the chord is complete.</summary>
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

    private static string Describe(IReadOnlyList<KeyName> chord) =>
        chord.Count == 0 ? "…" : PlanRenderer.DescribeTrigger(new HotkeyAI.Core.Dsl.Trigger { Keys = chord });

    private static Button Button(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Foreground = Palette.Text,
            Background = Palette.Selection,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            Cursor = Cursors.Hand,
            // Never focusable: a focused button steals Space and Enter from the capture.
            Focusable = false,
        };

        button.Click += (_, _) => onClick();
        return button;
    }
}

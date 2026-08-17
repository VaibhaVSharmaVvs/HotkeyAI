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

    /// <summary>Set once this window starts closing, so Deactivated stops acting.</summary>
    private bool closing;

    private HotkeyCaptureWindow(IDashboardHost host, string fileName, string name, string current)
    {
        this.host = host;
        this.fileName = fileName;

        Title = $"Hotkey for {name}";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Icon = TrayIcon.WindowIcon();
        Background = Palette.Surface;
        Foreground = Palette.Text;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(24) };

        layout.Children.Add(Fluent.Heading(name));
        layout.Children.Add(new TextBlock
        {
            Text = "Press the combination you want.",
            Foreground = Palette.Muted,
            FontSize = 12.5,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // The chord, drawn as the key it will become, so what you are pressing and what the row
        // will show are the same picture.
        chordText = new TextBlock
        {
            Text = current,
            Foreground = Palette.Text,
            FontSize = 24,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        layout.Children.Add(new Border
        {
            Background = Palette.Raised,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 16, 0, 10),
            Child = chordText,
        });

        verdict = new TextBlock
        {
            Text = "Waiting for a keypress.",
            Foreground = Palette.Muted,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        layout.Children.Add(verdict);

        layout.Children.Add(new TextBlock
        {
            // Said plainly rather than buried, because it is the one thing this window cannot
            // find out. See the availability caveat in the Phase 0 notes.
            Text = "A combination held by a low-level keyboard hook — some AutoHotkey scripts, "
                 + "push-to-talk — looks free here and then never fires.",
            Foreground = Palette.Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        });

        save = Fluent.Primary("Save", Fluent.Tick, Commit);
        save.IsEnabled = false;
        save.Focusable = false;

        var cancel = Fluent.IconButton(Fluent.Cross, "Cancel  (Esc)", Close);
        cancel.Focusable = false;

        layout.Children.Add(Fluent.Buttons(cancel, save));
        Content = layout;

        PreviewKeyDown += OnKey;

        // Capture only works while this window has the keyboard, and it can lose it without
        // being closed — pressing a shell-reserved combination such as Ctrl+Shift+Esc hands
        // focus elsewhere mid-capture. Left open it would sit there looking ready and quietly
        // recording nothing, so it closes instead of lying about being able to listen.
        //
        // Guarded, because closing is itself deactivating: WPF refuses Close() on a window that
        // is already closing, and the resulting InvalidOperationException took the agent down
        // once before the dispatcher started catching them. Saving a chord deactivates this
        // window on the way out, so the unguarded version threw every single time.
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) =>
        {
            if (!closing)
            {
                Close();
            }
        };
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

}

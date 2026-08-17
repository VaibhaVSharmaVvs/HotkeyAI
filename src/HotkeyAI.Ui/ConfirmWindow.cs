using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HotkeyAI.Ui;

/// <summary>
/// The yes/no gate in front of destructive actions.
/// </summary>
/// <remarks>
/// Safety control 5. Every default here points at "no": Escape, clicking away, Enter and the
/// focused button all decline. This prompt exists because something is about to kill a process,
/// and someone dismissing a dialog on reflex must not thereby confirm it.
/// </remarks>
internal sealed class ConfirmWindow : OverlayWindow
{
    internal ConfirmWindow(string message)
        : base(width: 520)
    {
        var layout = new StackPanel();

        // A warning glyph, in the danger colour. This prompt only ever appears because something
        // is about to be killed, and the colour says so before the sentence is read.
        var head = new Grid { Margin = new Thickness(20, 20, 20, 16) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var warning = Fluent.Glyph(Fluent.Alert, 20, Palette.Danger);
        warning.VerticalAlignment = VerticalAlignment.Top;
        warning.Margin = new Thickness(0, 1, 14, 0);
        Grid.SetColumn(warning, 0);
        head.Children.Add(warning);

        var text = new TextBlock
        {
            Text = message,
            Foreground = Palette.Text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        layout.Children.Add(head);

        var no = Button("Cancel", Palette.Text, () => Answer(false));
        var yes = Button("Continue", Palette.Danger, () => Answer(true));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        // Hints on the left, buttons on the right, on one line. The keys are the fast path and
        // deserve to be visible; deliberately not baked into the button labels, where "Continue
        // (Y)" reads as part of the action.
        var footer = new Grid { Margin = new Thickness(20, 0, 20, 18) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hints = Fluent.HintBar(
            Fluent.KeyHint("Y", "continue"),
            Fluent.KeyHint("Esc", "cancel"));

        hints.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(hints, 0);
        footer.Children.Add(hints);

        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        layout.Children.Add(footer);

        Card.Child = layout;

        // Y confirms; everything else, Enter included, declines.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Y)
            {
                Answer(true);
                e.Handled = true;
            }
            else if (e.Key is Key.N or Key.Enter)
            {
                Answer(false);
                e.Handled = true;
            }
        };

        Loaded += (_, _) => no.Focus();
    }

    /// <summary>True only if the user actively confirmed.</summary>
    internal bool Confirmed { get; private set; }

    internal bool Confirm()
    {
        ShowOverlay();
        return Confirmed;
    }

    protected override void Cancel() => Answer(false);

    private void Answer(bool confirmed)
    {
        Confirmed = confirmed;
        CloseOnce();
    }

    /// <remarks>
    /// Not <c>Fluent.Primary</c> for the confirming button, deliberately. A filled accent button
    /// is the visual default of a dialog, and nothing in this one may look like a default — the
    /// whole point is that a reflex dismissal declines.
    /// </remarks>
    private static Button Button(string text, Brush foreground, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12.5 },
            Foreground = foreground,
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Template = Fluent.OutlineButtonTemplate(foreground),
        };

        System.Windows.Automation.AutomationProperties.SetName(button, text);

        button.Click += (_, _) => onClick();
        return button;
    }
}

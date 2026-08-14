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

        layout.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Palette.Text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 18, 18, 14),
        });

        var no = Button("Cancel  (Esc)", Palette.Text, () => Answer(false));
        var yes = Button("Continue  (Y)", Palette.Danger, () => Answer(true));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 0, 18, 16),
        };

        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        layout.Children.Add(buttons);

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

    private static Button Button(string text, Brush foreground, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Foreground = foreground,
            Background = Palette.Selection,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            Cursor = Cursors.Hand,
        };

        button.Click += (_, _) => onClick();
        return button;
    }
}

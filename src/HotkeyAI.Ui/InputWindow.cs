using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HotkeyAI.Ui;

/// <summary>The single-field prompt behind <c>show_input</c>.</summary>
internal sealed class InputWindow : OverlayWindow
{
    private readonly TextBox entry;

    internal InputWindow(string prompt, string? defaultValue)
        : base(width: 560)
    {
        entry = new TextBox
        {
            Text = defaultValue ?? "",
            Background = Brushes.Transparent,
            Foreground = Palette.Text,
            CaretBrush = Palette.Accent,
            BorderThickness = new Thickness(0),
            FontSize = 18,
            Margin = new Thickness(16, 10, 16, 12),
        };

        entry.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
                e.Handled = true;
            }
        };

        var layout = new StackPanel();

        layout.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = Palette.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 14, 16, 0),
        });

        layout.Children.Add(entry);
        layout.Children.Add(new Border { Height = 1, Background = Palette.Edge });

        layout.Children.Add(new TextBlock
        {
            Text = "Enter confirms  ·  Esc cancels",
            Foreground = Palette.Muted,
            FontSize = 11,
            Margin = new Thickness(16, 8, 16, 10),
        });

        Card.Child = layout;

        Loaded += (_, _) =>
        {
            entry.Focus();

            // Selected rather than merely present, so typing replaces the suggestion. A default
            // the user has to clear by hand is worse than no default at all.
            entry.SelectAll();
        };
    }

    /// <summary>What the user typed, or null if they cancelled.</summary>
    internal string? Answer { get; private set; }

    internal string? Ask()
    {
        ShowOverlay();
        return Answer;
    }

    protected override void Cancel()
    {
        Answer = null;
        CloseOnce();
    }

    private void Accept()
    {
        Answer = entry.Text;
        CloseOnce();
    }
}

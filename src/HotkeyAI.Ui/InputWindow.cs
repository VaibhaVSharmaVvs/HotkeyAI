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
            FontSize = 19,
            Margin = new Thickness(20, 4, 20, 16),
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

        // The prompt is the loudest thing here, not a caption above the field. It is the question
        // being asked, and an overlay gets about a second to make that question land.
        layout.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = Palette.Text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 12),
        });

        layout.Children.Add(entry);
        layout.Children.Add(new Border { Height = 1, Background = Palette.Edge });

        var hints = Fluent.HintBar(
            Fluent.KeyHint("Enter", "confirm"),
            Fluent.KeyHint("Esc", "cancel"));

        hints.Margin = new Thickness(20, 11, 20, 13);
        layout.Children.Add(hints);

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

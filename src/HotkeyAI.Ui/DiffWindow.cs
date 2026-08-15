using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HotkeyAI.Core.Diff;

namespace HotkeyAI.Ui;

/// <summary>
/// What changed between two versions of a plan, and a button to accept it.
/// </summary>
/// <remarks>
/// The concept showed only the new plan. A new plan on its own is not reviewable: the question
/// worth answering is not "is this reasonable?" but "what did it change?", and those have very
/// different answers when a model has quietly dropped a step. Only the second one catches it.
/// <para>
/// Used for both directions this can happen — a repaired plan arriving from Claude Code, and an
/// old version being put back — because they are the same review with the arguments swapped.
/// </para>
/// </remarks>
internal sealed class DiffWindow : Window
{
    private DiffWindow(
        string title, string before, string after, string actionLabel, Func<string?> onAccept)
    {
        Title = title;
        Width = 900;
        Height = 700;
        Background = Palette.Surface;
        Foreground = Palette.Text;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var diff = LineDiff.Between(before, after);
        var (added, removed) = LineDiff.Summarise(diff);

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The headline a reviewer reads first: "+2 −1" on a repair is reassuring, "+40 −38" means
        // the plan was rewritten rather than fixed, and that is worth knowing before reading it.
        var summary = new TextBlock
        {
            Foreground = Palette.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
        };

        summary.Inlines.Add(new Run($"+{added} ") { Foreground = Palette.Accent });
        summary.Inlines.Add(new Run($"−{removed}") { Foreground = Palette.Danger });
        summary.Inlines.Add(new Run(added == 0 && removed == 0
            ? "   ·   identical"
            : "   ·   read this before accepting it"));

        Grid.SetRow(summary, 0);
        layout.Children.Add(summary);

        var lines = new StackPanel();

        foreach (var line in diff)
        {
            lines.Children.Add(Row(line));
        }

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Palette.Selection,
            Padding = new Thickness(8),
            Content = lines,
        };

        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        var said = new TextBlock
        {
            Foreground = Palette.Danger,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        buttons.Children.Add(Button("Cancel", Close, Palette.Text));
        buttons.Children.Add(Button(actionLabel, () =>
        {
            if (onAccept() is { } error)
            {
                said.Text = error;
                return;
            }

            DialogResult = true;
            Close();
        }, Palette.Accent));

        var footer = new StackPanel();
        footer.Children.Add(buttons);
        footer.Children.Add(said);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        Content = layout;

        SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    /// <summary>Show the diff; returns true if the user accepted it.</summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="title">Caption.</param>
    /// <param name="before">The version being replaced.</param>
    /// <param name="after">The version being proposed.</param>
    /// <param name="actionLabel">What the accept button says.</param>
    /// <param name="onAccept">Applies the change. Returns null on success, or the reason not to.</param>
    internal static bool Show(
        Window owner,
        string title,
        string before,
        string after,
        string actionLabel,
        Func<string?> onAccept)
    {
        var window = new DiffWindow(title, before, after, actionLabel, onAccept) { Owner = owner };
        return window.ShowDialog() == true;
    }

    private static Border Row(DiffLine line)
    {
        // A marker as well as a colour. Colour alone is unreadable to a good number of people, and
        // this is the screen where missing a removed line matters most.
        var (marker, ink, back) = line.Kind switch
        {
            DiffKind.Added => ("+", Palette.Accent, Colour(0x22, 0x3A, 0x2A)),
            DiffKind.Removed => ("−", Palette.Danger, Colour(0x3A, 0x24, 0x24)),
            _ => (" ", Palette.Muted, Brushes.Transparent),
        };

        var text = new TextBlock
        {
            Text = $"{marker} {line.Text}",
            Foreground = ink,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
        };

        return new Border
        {
            Background = back,
            Padding = new Thickness(6, 1, 6, 1),
            Child = text,
        };
    }

    private static SolidColorBrush Colour(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Button Button(string text, Action onClick, Brush foreground)
    {
        var button = new Button
        {
            Content = text,
            Foreground = foreground,
            Background = Palette.Edge,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        button.Click += (_, _) => onClick();
        return button;
    }
}

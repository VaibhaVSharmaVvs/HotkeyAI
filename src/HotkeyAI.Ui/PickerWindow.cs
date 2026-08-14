using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HotkeyAI.Core.Matching;

namespace HotkeyAI.Ui;

/// <summary>
/// The fuzzy-search picker: the overlay behind <c>show_picker</c>.
/// </summary>
/// <remarks>
/// Keyboard only, by design. It is opened by a keypress in the middle of an automation, and
/// reaching for the mouse to answer it would cost more time than the automation saves. Ranking
/// lives in <see cref="FuzzyMatcher"/> in Core, where it is unit tested; this class renders the
/// result and handles the keys.
/// </remarks>
internal sealed class PickerWindow : OverlayWindow
{
    /// <summary>Rows visible before the list scrolls.</summary>
    private const int VisibleRows = 9;

    private readonly IReadOnlyList<string> items;
    private readonly TextBox query;
    private readonly ListBox results;
    private readonly TextBlock status;

    private IReadOnlyList<(int Index, FuzzyResult Result)> ranked = [];

    internal PickerWindow(IReadOnlyList<string> items, string? prompt)
        : base(width: 640)
    {
        this.items = items;

        query = new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = Palette.Text,
            CaretBrush = Palette.Accent,
            BorderThickness = new Thickness(0),
            FontSize = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(16, 12, 16, 12),
        };

        query.TextChanged += (_, _) => Refresh();
        query.PreviewKeyDown += OnKeyDown;

        results = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // The text box keeps focus throughout; the list is driven from the keyboard, so it
            // must never take focus itself or typing would stop reaching the query.
            Focusable = false,
            ItemContainerStyle = RowStyle(),
            Margin = new Thickness(0, 0, 0, 4),
        };

        results.MouseDoubleClick += (_, _) => Accept();
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);

        status = new TextBlock
        {
            Foreground = Palette.Muted,
            FontSize = 11,
            Margin = new Thickness(16, 8, 16, 10),
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var caption = new TextBlock
            {
                Text = prompt,
                Foreground = Palette.Muted,
                FontSize = 12,
                Margin = new Thickness(16, 12, 16, 0),
            };

            Grid.SetRow(caption, 0);
            layout.Children.Add(caption);
        }

        Grid.SetRow(query, 1);
        layout.Children.Add(query);

        var divider = new Border
        {
            Height = 1,
            Background = Palette.Edge,
        };

        Grid.SetRow(divider, 2);
        layout.Children.Add(divider);

        var lower = new StackPanel();
        lower.Children.Add(results);
        lower.Children.Add(status);
        Grid.SetRow(lower, 3);
        layout.Children.Add(lower);

        Card.Child = layout;

        Refresh();
        Loaded += (_, _) => query.Focus();
    }

    /// <summary>The chosen item, or null if the user cancelled.</summary>
    internal string? Selection { get; private set; }

    /// <summary>Show the picker and block until the user answers.</summary>
    internal string? Pick()
    {
        ShowOverlay();
        return Selection;
    }

    protected override void Cancel()
    {
        Selection = null;
        CloseOnce();
    }

    private void Accept()
    {
        if (results.SelectedIndex >= 0 && results.SelectedIndex < ranked.Count)
        {
            Selection = items[ranked[results.SelectedIndex].Index];
            CloseOnce();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;

            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;

            case Key.PageDown:
                Move(VisibleRows);
                e.Handled = true;
                break;

            case Key.PageUp:
                Move(-VisibleRows);
                e.Handled = true;
                break;

            case Key.Enter:
                Accept();
                e.Handled = true;
                break;

            // Ctrl+N / Ctrl+P as well as the arrows: the people most likely to live in a picker
            // are the ones who already expect these to work.
            case Key.N when e.KeyboardDevice.Modifiers == ModifierKeys.Control:
                Move(1);
                e.Handled = true;
                break;

            case Key.P when e.KeyboardDevice.Modifiers == ModifierKeys.Control:
                Move(-1);
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void Move(int delta)
    {
        if (ranked.Count == 0)
        {
            return;
        }

        // Clamped rather than wrapped. Wrapping means holding Down past the end silently jumps
        // back to the top, and the item under the cursor is no longer the one being read.
        var next = Math.Clamp(results.SelectedIndex + delta, 0, ranked.Count - 1);
        results.SelectedIndex = next;
        results.ScrollIntoView(results.Items[next]);
    }

    private void Refresh()
    {
        ranked = FuzzyMatcher.Rank(items, query.Text);

        results.Items.Clear();

        foreach (var (index, result) in ranked)
        {
            results.Items.Add(new ListBoxItem
            {
                Content = Row(items[index], result.Positions),
                Focusable = false,
            });
        }

        results.MaxHeight = VisibleRows * 34;
        results.SelectedIndex = ranked.Count > 0 ? 0 : -1;

        if (ranked.Count > 0)
        {
            results.ScrollIntoView(results.Items[0]);
        }

        status.Text = ranked.Count switch
        {
            0 when items.Count > 0 => $"No match  ·  {items.Count} item(s)  ·  Esc cancels",
            _ => $"{ranked.Count} of {items.Count}  ·  ↑↓ move  ·  Enter select  ·  Esc cancel",
        };
    }

    /// <summary>
    /// Render one row: the leaf name, and the full value beneath it when they differ.
    /// </summary>
    /// <remarks>
    /// Items are usually absolute paths, where the part that identifies the item is the last
    /// segment and the rest is shared boilerplate. Showing only the full path makes every row
    /// look alike at a glance; showing only the leaf hides which of two same-named folders this
    /// is. Both, with the match highlighted in each, answers both questions.
    /// </remarks>
    private static StackPanel Row(string item, IReadOnlyList<int> positions)
    {
        var leafStart = LeafStart(item);
        var stack = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };

        stack.Children.Add(Highlighted(
            item[leafStart..],
            positions.Where(p => p >= leafStart).Select(p => p - leafStart).ToList(),
            Palette.Text,
            fontSize: 14));

        if (leafStart > 0)
        {
            stack.Children.Add(Highlighted(item, positions, Palette.Muted, fontSize: 11));
        }

        return stack;
    }

    private static TextBlock Highlighted(
        string text, IReadOnlyList<int> positions, Brush foreground, double fontSize)
    {
        var block = new TextBlock
        {
            Foreground = foreground,
            FontSize = fontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var marked = new HashSet<int>(positions);
        var run = new System.Text.StringBuilder();
        var runIsMatch = false;

        void Flush()
        {
            if (run.Length == 0)
            {
                return;
            }

            block.Inlines.Add(runIsMatch
                ? new Run(run.ToString()) { Foreground = Palette.Accent, FontWeight = FontWeights.Bold }
                : new Run(run.ToString()));

            run.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var isMatch = marked.Contains(i);

            if (isMatch != runIsMatch)
            {
                Flush();
                runIsMatch = isMatch;
            }

            run.Append(text[i]);
        }

        Flush();
        return block;
    }

    private static int LeafStart(string item)
    {
        var slash = item.LastIndexOfAny(['\\', '/']);
        return slash >= 0 && slash < item.Length - 1 ? slash + 1 : 0;
    }

    private static Style RowStyle()
    {
        var style = new Style(typeof(ListBoxItem));

        style.Setters.Add(new Setter(TemplateProperty, RowTemplate()));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 2, 12, 2)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        return style;
    }

    /// <summary>
    /// A row template with no hover or inactive-selection states.
    /// </summary>
    /// <remarks>
    /// The default ListBox chrome greys the selection out when the control does not have focus —
    /// and it never does here, because focus stays in the text box. Without this the selected
    /// row is invisible, which is the one thing the user has to be able to see.
    /// </remarks>
    private static ControlTemplate RowTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));

        // Named, because the trigger below has to target this Border. Without a TargetName the
        // setter applies to the templated ListBoxItem instead, whose background is not what is
        // drawn — so the selected row renders identically to every other one and the user cannot
        // see what Enter is about to choose.
        var border = new FrameworkElementFactory(typeof(Border), "row");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(MarginProperty, new Thickness(6, 1, 6, 1));
        border.SetValue(PaddingProperty, new Thickness(8, 4, 8, 4));
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };

        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Selection, "row"));
        template.Triggers.Add(selected);

        return template;
    }
}

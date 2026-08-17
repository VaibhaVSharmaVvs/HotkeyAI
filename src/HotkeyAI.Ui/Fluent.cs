using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HotkeyAI.Ui;

/// <summary>
/// The small controls the dashboard is built from, in the shape Windows 11 settings use.
/// </summary>
/// <remarks>
/// Hand-built because WPF ships none of them: there is no switch, no search field, and the
/// stock CheckBox and Button carry a light-theme chrome that cannot be recoloured into
/// something that belongs next to the Windows 11 shell. Templates are assembled with
/// <see cref="FrameworkElementFactory"/>, which is what <c>TrayMenu</c> and <c>PickerWindow</c>
/// already do.
/// <para>
/// Every icon here is decorative and every one sits beside a word. Segoe Fluent Icons ships
/// with Windows 11 and MDL2 with Windows 10, and naming both lets WPF fall back — but a machine
/// with neither renders a row of empty boxes, and a toolbar that has become unreadable must
/// still be usable.
/// </para>
/// </remarks>
internal static class Fluent
{
    /// <summary>Segoe Fluent Icons, falling back to the Windows 10 set.</summary>
    private static readonly FontFamily IconFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Code points, named so a reader does not have to look them up.
    internal const string Refresh = "";
    internal const string Folder = "";
    internal const string Document = "";
    internal const string Search = "";
    internal const string ChevronDown = "";
    internal const string Tick = "";
    internal const string Cross = "";
    internal const string Keyboard = "";
    internal const string Play = "";
    internal const string History = "";
    internal const string Repair = "";
    internal const string Read = "";
    internal const string Add = "";

    /// <summary>A decorative glyph. Never the only thing saying what something does.</summary>
    internal static TextBlock Glyph(string code, double size = 14, Brush? ink = null) => new()
    {
        Text = code,
        FontFamily = IconFont,
        FontSize = size,
        Foreground = ink ?? Palette.Text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The status dot: on and running, on but dead, or off.</summary>
    internal static Ellipse Dot(Brush fill, double size = 9) => new()
    {
        Width = size,
        Height = size,
        Fill = fill,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// A pill switch, the way Windows 11 draws on and off.
    /// </summary>
    /// <remarks>
    /// A templated <see cref="ToggleButton"/> rather than a CheckBox with a new look, so it
    /// reports itself to UI Automation as the toggle it is. That matters here: the dashboard's
    /// checkboxes were once wired to Click, which fires for mouse and keyboard but not for UI
    /// Automation, so a screen reader could move the switch and change nothing.
    /// </remarks>
    internal static ToggleButton Switch(bool on, Action<bool> changed, string? tooltip = null)
    {
        var toggle = new ToggleButton
        {
            IsChecked = on,
            Width = 40,
            Height = 20,
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Center,
            Template = SwitchTemplate(),
        };

        // Checked and Unchecked, not Click: see the remarks above.
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    private static ControlTemplate SwitchTemplate()
    {
        var track = new FrameworkElementFactory(typeof(Border), "track");
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        track.SetValue(Border.BackgroundProperty, Palette.Edge);
        track.SetValue(Border.BorderBrushProperty, Palette.Muted);
        track.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var thumb = new FrameworkElementFactory(typeof(Ellipse), "thumb");
        thumb.SetValue(FrameworkElement.WidthProperty, 12.0);
        thumb.SetValue(FrameworkElement.HeightProperty, 12.0);
        thumb.SetValue(Shape.FillProperty, Palette.Text);
        thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));

        track.AppendChild(thumb);

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = track };

        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Accent, "track"));
        on.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "track"));
        on.Setters.Add(new Setter(Shape.FillProperty, Palette.Surface, "thumb"));
        on.Setters.Add(new Setter(
            FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "thumb"));
        on.Setters.Add(new Setter(
            FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0), "thumb"));
        template.Triggers.Add(on);

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Text, "track"));
        template.Triggers.Add(hover);

        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "track"));
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "track"));
        template.Triggers.Add(focused);

        return template;
    }

    /// <summary>A flat button carrying a glyph and a word.</summary>
    internal static Button IconButton(
        string glyph, string text, Action onClick, Brush? ink = null, string? tooltip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Glyph(glyph, 14, ink ?? Palette.Text));
        content.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
        });

        var button = new Button
        {
            Content = content,
            Foreground = ink ?? Palette.Text,
            Padding = new Thickness(12, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Template = FlatButtonTemplate(),
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A flat button with only a glyph. Needs a tooltip, and always has one.</summary>
    internal static Button GlyphButton(
        string glyph, string tooltip, Action onClick, Brush? ink = null)
    {
        var button = new Button
        {
            Content = Glyph(glyph, 14, ink ?? Palette.Muted),
            Foreground = ink ?? Palette.Muted,
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Template = FlatButtonTemplate(),
        };

        // The tooltip is the only label, so it also has to reach a screen reader.
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);

        button.Click += (_, _) => onClick();
        return button;
    }

    private static ControlTemplate FlatButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, Palette.Edge);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.PaddingProperty,
            new System.Windows.Data.Binding(nameof(Control.Padding))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Palette.RaisedHover, "bg"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Selection, "bg"));
        template.Triggers.Add(pressed);

        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "bg"));
        template.Triggers.Add(focused);

        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "bg"));
        template.Triggers.Add(off);

        return template;
    }

    /// <summary>
    /// A search field with a magnifier and placeholder text.
    /// </summary>
    /// <remarks>
    /// The placeholder is a separate TextBlock behind the box rather than text inside it. Real
    /// placeholder text has to be cleared on focus, and every implementation that does that
    /// eventually submits the placeholder as a search — here it is simply hidden when there is
    /// anything to show.
    /// </remarks>
    internal static Grid SearchBox(TextBox box, string placeholder)
    {
        ArgumentNullException.ThrowIfNull(box);

        box.Background = Brushes.Transparent;
        box.Foreground = Palette.Text;
        box.CaretBrush = Palette.Accent;
        box.BorderThickness = new Thickness(0);
        box.FontSize = 13;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Padding = new Thickness(0);

        var hint = new TextBlock
        {
            Text = placeholder,
            Foreground = Palette.Muted,
            FontSize = 13,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Sync() => hint.Visibility =
            box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        box.TextChanged += (_, _) => Sync();
        Sync();

        var inner = new Grid();
        inner.Children.Add(hint);
        inner.Children.Add(box);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(inner, 0);
        row.Children.Add(inner);

        var magnifier = Glyph(Search, 13, Palette.Muted);
        magnifier.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(magnifier, 1);
        row.Children.Add(magnifier);

        var shell = new Border
        {
            Background = Palette.Raised,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 7),
            Child = row,
        };

        // Focus follows the whole field, not just the twelve pixels of text inside it.
        shell.MouseLeftButtonDown += (_, _) => box.Focus();
        box.GotKeyboardFocus += (_, _) => shell.BorderBrush = Palette.Accent;
        box.LostKeyboardFocus += (_, _) => shell.BorderBrush = Palette.Edge;

        var host = new Grid();
        host.Children.Add(shell);
        return host;
    }

    /// <summary>A card: the surface every row and panel sits on.</summary>
    internal static Border Card(UIElement child, double radius = 8) => new()
    {
        Background = Palette.Raised,
        BorderBrush = Palette.Edge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Child = child,
    };

    /// <summary>A hairline, for separating an expanded panel from its row.</summary>
    internal static Border Divider() => new()
    {
        Height = 1,
        Background = Palette.Edge,
        Margin = new Thickness(0, 10, 0, 10),
    };
}

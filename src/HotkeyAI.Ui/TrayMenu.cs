using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HotkeyAI.Ui;

/// <summary>
/// The tray's context menu, drawn in WPF.
/// </summary>
/// <remarks>
/// This replaced a WinForms <c>ContextMenuStrip</c>, for a reason worth recording. WPF sets the
/// process to per-monitor DPI awareness at startup; WinForms in the same process does not follow
/// unless separately configured, so its ToolStrip measured itself at 96 DPI while the shell drew
/// it scaled. On a 125% display the result was a menu with truncated labels and scroll arrows, in
/// which <b>Quit was below the fold and unreachable</b> — the one item a user must always be able
/// to reach. A WPF menu shares the framework that already owns the overlays and the dashboard, so
/// it is measured in the same units as everything else.
/// </remarks>
internal static class TrayMenu
{
    /// <summary>
    /// Show the menu at the cursor.
    /// </summary>
    /// <remarks>
    /// Must be called on the UI thread.
    /// </remarks>
    public static void Show(IReadOnlyList<TrayCommand> commands)
    {
        // A tray menu has no window to belong to, and Windows will not dismiss a popup raised by
        // a process that does not own the foreground — the same rule that has applied to
        // TrackPopupMenu since Win32. Without this anchor the menu opens and then stays open,
        // ignoring clicks elsewhere. It is off-screen, borderless and closes with the menu.
        var anchor = new Window
        {
            Width = 0,
            Height = 0,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
        };

        anchor.Show();
        HotkeyAI.Windows.ForegroundWindow.Force(new WindowInteropHelper(anchor).Handle);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = anchor,
            StaysOpen = false,
            Style = MenuStyle(),
        };

        foreach (var command in commands)
        {
            menu.Items.Add(Item(command, menu));
        }

        menu.Closed += (_, _) => anchor.Close();
        menu.IsOpen = true;
    }

    private static object Item(TrayCommand command, ContextMenu menu)
    {
        if (command.OnClick is null && command.Text == "-")
        {
            return new Separator { Style = SeparatorStyle() };
        }

        // A caption, not a command: the count at the top of the menu. Rendered as muted text
        // rather than a disabled menu item, so it does not look like something that failed.
        if (command.OnClick is null)
        {
            return new MenuItem
            {
                Header = command.Text,
                IsEnabled = false,
                Style = CaptionStyle(),
            };
        }

        var item = new MenuItem
        {
            Header = command.Text,
            IsEnabled = command.Enabled,
            Style = ItemStyle(),
            Icon = Glyph(command.Checked ? "" : command.Glyph),
        };

        item.Click += (_, _) =>
        {
            menu.IsOpen = false;
            command.OnClick();
        };

        return item;
    }

    private static TextBlock? Glyph(string? glyph) => glyph is null
        ? null
        : new TextBlock
        {
            Text = glyph,
            // Segoe Fluent Icons ships with Windows 11, MDL2 with 10. Naming both lets WPF fall
            // back rather than render notdef boxes on an older build.
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

    private static Style MenuStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Palette.Surface);
        border.SetValue(Border.BorderBrushProperty, Palette.Edge);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(8));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        border.SetValue(UIElement.EffectProperty, new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 3,
            Direction = 270,
            Opacity = 0.45,
            Color = Colors.Black,
        });

        var items = new FrameworkElementFactory(typeof(StackPanel));
        items.SetValue(StackPanel.IsItemsHostProperty, true);
        border.AppendChild(items);

        var template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };

        var style = new Style(typeof(ContextMenu));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        // Transparent so the rounded corners and shadow are not drawn on a grey square.
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
        return style;
    }

    private static Style ItemStyle() => RowStyle(Palette.Text, interactive: true);

    private static Style CaptionStyle() => RowStyle(Palette.Muted, interactive: false);

    private static Style RowStyle(Brush foreground, bool interactive)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bg");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 14, 7));

        var grid = new FrameworkElementFactory(typeof(Grid));
        var iconColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
        iconColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(24));
        var textColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
        textColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        grid.AppendChild(iconColumn);
        grid.AppendChild(textColumn);

        var icon = new FrameworkElementFactory(typeof(ContentPresenter));
        icon.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
        icon.SetValue(Grid.ColumnProperty, 0);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        text.SetValue(Grid.ColumnProperty, 1);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(text);

        border.AppendChild(grid);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };

        if (interactive)
        {
            var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Selection, "bg"));
            template.Triggers.Add(hover);
        }

        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.FontSizeProperty, interactive ? 13.0 : 11.0));
        return style;
    }

    private static Style SeparatorStyle()
    {
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(Border.BackgroundProperty, Palette.Edge);
        line.SetValue(FrameworkElement.HeightProperty, 1.0);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 4, 10, 4));

        var style = new Style(typeof(Separator));
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            new ControlTemplate(typeof(Separator)) { VisualTree = line }));

        return style;
    }
}

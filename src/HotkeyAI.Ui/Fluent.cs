using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // Code points as explicit escapes, not as pasted characters.
    //
    // Written literally, these are private-use characters that no editor renders and no diff
    // shows — and two of them silently ended up as the wrong glyph, so "Copy prompt for Claude
    // Code" carried a warning triangle and the back arrow rendered as an empty box. An escape is
    // greppable, survives every encoding, and can be checked against the Segoe MDL2 chart.
    internal const string Refresh = "";        // Refresh
    internal const string Folder = "";         // FolderOpen
    internal const string Document = "";       // Document
    internal const string Search = "";         // Search
    internal const string ChevronDown = "";    // ChevronDown
    internal const string Back = "";           // Back
    internal const string Tick = "";           // CheckMark
    internal const string Cross = "";          // Cancel
    internal const string Keyboard = "";       // KeyboardClassic
    internal const string Play = "";           // Play
    internal const string History = "";        // History
    internal const string Repair = "";         // Repair
    internal const string Read = "";           // Page
    internal const string Add = "";            // Add
    internal const string Alert = "";          // Warning
    internal const string Info = "";           // Info
    internal const string Error = "";          // ErrorBadge

    /// <summary>A decorative glyph. Never the only thing saying what something does.</summary>
    internal static TextBlock Glyph(string code, double size = 14, Brush? ink = null) => new()
    {
        Text = code,
        FontFamily = IconFont,
        FontSize = size,
        Foreground = ink ?? Palette.Text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The status dot, lit like a backlight under a keycap.
    /// </summary>
    /// <remarks>
    /// A halo behind the dot rather than a flat disc. This is the one place the interface is
    /// allowed to look like the hardware it drives, and it costs nothing: a lit dot reads as a
    /// state the machine is reporting, where a painted circle reads as a label someone typed.
    /// </remarks>
    internal static Grid Dot(SolidColorBrush fill, double size = 9)
    {
        ArgumentNullException.ThrowIfNull(fill);

        var host = new Grid
        {
            Width = size * 2.4,
            Height = size * 2.4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        host.Children.Add(new Ellipse
        {
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Palette.Backlight(fill, 0.42).Color, 0.0),
                    new GradientStop(Palette.Backlight(fill, 0.0).Color, 1.0),
                },
            },
        });

        host.Children.Add(new Ellipse
        {
            Width = size,
            Height = size,
            Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return host;
    }

    // ----------------------------- motion -----------------------------

    /// <summary>
    /// How this application moves.
    /// </summary>
    /// <remarks>
    /// Fast, and out of the way. The dashboard is opened for seconds to answer one question, and
    /// an overlay appears in the middle of somebody's keystroke — anything that has to be waited
    /// out is a cost, not polish. Everything here is under a quarter of a second and eases out,
    /// never in: an ease-in delays the first frame, which is the frame the user is watching.
    /// <para>
    /// Only <c>Opacity</c> and transforms are animated. Width and height changes cost a layout
    /// pass per frame, which is exactly how a smooth idea turns into a stuttering one — the row
    /// expansion below is the single deliberate exception, and it is short.
    /// </para>
    /// </remarks>
    internal static class Motion
    {
        /// <summary>Hover and colour shifts. Barely a duration at all.</summary>
        internal static readonly Duration Quick = new(TimeSpan.FromMilliseconds(120));

        /// <summary>Switches, chevrons, anything the user just clicked.</summary>
        internal static readonly Duration Snap = new(TimeSpan.FromMilliseconds(170));

        /// <summary>Overlays and panels arriving.</summary>
        internal static readonly Duration Enter = new(TimeSpan.FromMilliseconds(200));

        /// <summary>
        /// The curve. Decelerating hard, so movement starts immediately and settles softly —
        /// which is roughly what a key does when you let it go.
        /// </summary>
        internal static IEasingFunction Ease { get; } =
            new CubicEase { EasingMode = EasingMode.EaseOut };

        /// <summary>Fade something in, from wherever it currently is.</summary>
        internal static void FadeIn(UIElement element, Duration over)
        {
            ArgumentNullException.ThrowIfNull(element);

            element.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(element.Opacity, 1, over) { EasingFunction = Ease });
        }

        /// <summary>Turn something, in place.</summary>
        internal static void RotateTo(UIElement element, double degrees, Duration over)
        {
            ArgumentNullException.ThrowIfNull(element);

            if (element.RenderTransform is not RotateTransform turn)
            {
                turn = new RotateTransform(0);
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                element.RenderTransform = turn;
            }

            turn.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(degrees, over) { EasingFunction = Ease });
        }

        /// <summary>
        /// Reveal or hide a panel by its own height.
        /// </summary>
        /// <remarks>
        /// The exception to the transform-only rule, and worth it: a panel that slides its content
        /// out of the way is the one animation in this window that explains what happened. It is
        /// measured first and released back to automatic height on arrival, so a row that later
        /// grows — a longer failure reason, a complaint typed in — is not pinned to the height it
        /// happened to have when it opened.
        /// </remarks>
        internal static void Reveal(FrameworkElement panel, bool open, Action? onClosed = null)
        {
            ArgumentNullException.ThrowIfNull(panel);

            if (open)
            {
                panel.Visibility = Visibility.Visible;

                // Measured at the width it will actually be laid out at, taken from the parent.
                // Measuring against a guess is how a reveal ends up overshooting and snapping
                // back: wrapping text is taller at 600px than at 940, so the animation would
                // climb to a height the panel never needs and then jump down to the real one.
                var width = panel.ActualWidth;

                if (width <= 0 && panel.Parent is FrameworkElement parent && parent.ActualWidth > 0)
                {
                    width = Math.Max(0, parent.ActualWidth - panel.Margin.Left - panel.Margin.Right);
                }

                panel.Measure(new Size(
                    width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));

                var target = panel.DesiredSize.Height;
                var grow = new DoubleAnimation(0, target, Snap) { EasingFunction = Ease };

                grow.Completed += (_, _) =>
                {
                    panel.BeginAnimation(FrameworkElement.HeightProperty, null);
                    panel.Height = double.NaN;
                };

                panel.BeginAnimation(FrameworkElement.HeightProperty, grow);
                panel.BeginAnimation(
                    UIElement.OpacityProperty, new DoubleAnimation(0, 1, Snap) { EasingFunction = Ease });
                return;
            }

            // Exits are faster than entrances. Leaving should not be something you wait for.
            var shrink = new DoubleAnimation(panel.ActualHeight, 0, Quick) { EasingFunction = Ease };

            shrink.Completed += (_, _) =>
            {
                panel.Visibility = Visibility.Collapsed;
                panel.BeginAnimation(FrameworkElement.HeightProperty, null);
                panel.Height = double.NaN;
                onClosed?.Invoke();
            };

            panel.BeginAnimation(FrameworkElement.HeightProperty, shrink);
            panel.BeginAnimation(
                UIElement.OpacityProperty, new DoubleAnimation(1, 0, Quick) { EasingFunction = Ease });
        }
    }

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

        // Named explicitly. WPF derives a button's accessible name from its content, and this
        // one's content is a panel — so without this the button reaches a screen reader, and UI
        // Automation generally, as an unnamed control.
        System.Windows.Automation.AutomationProperties.SetName(button, text);

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

        // Pressing shrinks it a fraction. Tactile confirmation that the click landed, and the one
        // piece of motion here that is deliberately instant — a press you can watch arrive is a
        // press that felt slow.
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(
            UIElement.RenderTransformProperty, new ScaleTransform(0.97, 0.97), "bg"));
        pressed.Setters.Add(new Setter(
            UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5), "bg"));
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

    /// <summary>
    /// A checkbox dark enough to sit next to the rest of this window.
    /// </summary>
    /// <remarks>
    /// <paramref name="ink"/> is the colour it takes when ticked, so the two verdicts can be
    /// green and red rather than both being the accent. Driven by Checked and Unchecked for the
    /// same UI Automation reason as <see cref="Switch"/>.
    /// </remarks>
    internal static CheckBox Check(string text, bool on, Action<bool> changed, Brush ink)
    {
        var box = new CheckBox
        {
            IsChecked = on,
            Content = text,
            Foreground = on ? ink : Palette.Muted,
            FontSize = 12.5,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
            Template = CheckTemplate(ink),
        };

        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    private static ControlTemplate CheckTemplate(Brush ink)
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var box = new FrameworkElementFactory(typeof(Border), "box");
        box.SetValue(FrameworkElement.WidthProperty, 18.0);
        box.SetValue(FrameworkElement.HeightProperty, 18.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        box.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        box.SetValue(Border.BorderBrushProperty, Palette.Muted);
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var tick = new FrameworkElementFactory(typeof(TextBlock), "tick");
        tick.SetValue(TextBlock.TextProperty, Tick);
        tick.SetValue(TextBlock.FontFamilyProperty, IconFont);
        tick.SetValue(TextBlock.FontSizeProperty, 10.0);
        tick.SetValue(TextBlock.ForegroundProperty, Palette.Surface);
        tick.SetValue(UIElement.OpacityProperty, 0.0);
        tick.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        tick.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.AppendChild(tick);
        row.AppendChild(box);

        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(label);

        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = row };

        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, ink, "box"));
        on.Setters.Add(new Setter(Border.BorderBrushProperty, ink, "box"));
        on.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "tick"));
        template.Triggers.Add(on);

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, ink, "box"));
        template.Triggers.Add(hover);

        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "box"));
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "box"));
        template.Triggers.Add(focused);

        return template;
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

    /// <summary>
    /// A slim, dark scrollbar with no arrow buttons.
    /// </summary>
    /// <remarks>
    /// WPF's stock ScrollBar is a light-theme control with a raised thumb and a stepper at each
    /// end. It is the one piece of chrome in the dashboard that cannot be recoloured through
    /// properties, so it has to be rebuilt: without this, a window that is otherwise entirely
    /// dark grows a strip of Windows 7 down its right edge the moment the list overflows.
    /// </remarks>
    /// <remarks>
    /// Parsed from markup rather than assembled with <see cref="FrameworkElementFactory"/> like
    /// everything else here, because a scrollbar needs a <c>Track</c> and a Track takes its
    /// Thumb through a plain CLR property rather than a dependency property — there is nothing
    /// for the factory to set. The markup is a constant, so it either parses on the first open
    /// or never.
    /// </remarks>
    internal static Style SlimScrollBar()
    {
        var idle = Palette.Edge.Color.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hot = Palette.Muted.Color.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var markup =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
            "<Setter Property='Width' Value='10'/>" +
            "<Setter Property='Background' Value='Transparent'/>" +
            "<Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ScrollBar'><Border Background='Transparent'>" +
            // Track alone: no RepeatButtons, so no stepper arrows and no grey gutter.
            "<Track x:Name='PART_Track' IsDirectionReversed='True'><Track.Thumb><Thumb>" +
            "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            $"<Border x:Name='bar' Background='{idle}' CornerRadius='3' Margin='3,0,3,0'/>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
            $"<Setter TargetName='bar' Property='Background' Value='{hot}'/>" +
            "</Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template>" +
            "</Thumb></Track.Thumb></Track></Border></ControlTemplate>" +
            "</Setter.Value></Setter></Style>";

        return (Style)System.Windows.Markup.XamlReader.Parse(markup);
    }

    /// <summary>
    /// An expander whose header is a glyph and a word, not a circled arrow.
    /// </summary>
    /// <remarks>
    /// WPF draws its expander toggle as a white circle with a chevron in it, which is the one
    /// light-theme artefact left in an otherwise dark window.
    /// </remarks>
    internal static ControlTemplate ExpanderTemplate(string glyph)
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));

        // The glyph and the caption are built here, inside the Expander's own template, so that
        // TemplatedParent resolves to the Expander. Built inside the ToggleButton's template
        // instead, it resolves to the button — which has no Header, so the caption silently
        // renders as nothing and the expander becomes a lone plus sign.
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, glyph);
        icon.SetValue(TextBlock.FontFamilyProperty, IconFont);
        icon.SetValue(TextBlock.FontSizeProperty, 13.0);
        icon.SetValue(TextBlock.ForegroundProperty, Palette.Accent);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(icon);

        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(Expander.Header))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });
        caption.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
        caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        caption.SetValue(TextBlock.ForegroundProperty, Palette.Text);
        caption.SetValue(TextBlock.FontSizeProperty, 13.0);
        row.AppendChild(caption);

        var header = new FrameworkElementFactory(typeof(ToggleButton), "toggle");
        header.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        header.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        header.SetBinding(ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(Expander.IsExpanded))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
                Mode = System.Windows.Data.BindingMode.TwoWay,
            });

        header.AppendChild(row);
        header.SetValue(Control.TemplateProperty, HeaderChromeTemplate());
        stack.AppendChild(header);

        var content = new FrameworkElementFactory(typeof(ContentPresenter), "content");
        content.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 8, 2, 0));
        stack.AppendChild(content);

        var template = new ControlTemplate(typeof(Expander)) { VisualTree = stack };
        var open = new Trigger { Property = Expander.IsExpandedProperty, Value = true };
        open.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "content"));
        template.Triggers.Add(open);

        return template;
    }

    /// <summary>A full-width row in a list: a card that lights up under the pointer.</summary>
    internal static ControlTemplate ListButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "row");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Palette.Raised);
        border.SetValue(Border.BorderBrushProperty, Palette.Edge);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.PaddingProperty, Templated(nameof(Control.Padding)));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, RaisedHoverBrush, "row"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "row"));
        template.Triggers.Add(hover);

        // Dimmed rather than hidden: the version already on disk still belongs in the list, it
        // just has nothing to restore.
        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "row"));
        template.Triggers.Add(off);

        return template;
    }

    private static Brush RaisedHoverBrush => Palette.RaisedHover;

    /// <summary>Chrome for a header that highlights on hover and holds whatever it is given.</summary>
    private static ControlTemplate HeaderChromeTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "hb");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 12, 7));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Raised, "hb"));
        template.Triggers.Add(over);

        return template;
    }

    /// <summary>A hairline, for separating an expanded panel from its row.</summary>
    internal static Border Divider() => new()
    {
        Height = 1,
        Background = Palette.Edge,
        Margin = new Thickness(0, 10, 0, 10),
    };

    // ----------------------------- overlays -----------------------------

    /// <summary>
    /// A key drawn as a key, followed by what it does.
    /// </summary>
    /// <remarks>
    /// For the footer of an overlay, where the whole message has to land in about a second.
    /// "Enter confirms · Esc cancels" as running text makes the reader parse a sentence; the same
    /// information with the keys drawn as keys is recognised rather than read, and it matches the
    /// keycaps the dashboard already uses for a chord.
    /// </remarks>
    internal static StackPanel KeyHint(string key, string does)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 16, 0),
        };

        row.Children.Add(new Border
        {
            Background = Palette.Selection,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = key,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Palette.Text,
            },
        });

        row.Children.Add(new TextBlock
        {
            Text = does,
            Foreground = Palette.Muted,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
        });

        return row;
    }

    /// <summary>
    /// An outlined button that takes its colour from what it does.
    /// </summary>
    /// <remarks>
    /// For places where nothing may look like the default — the confirm overlay, where a reflex
    /// press has to decline. A filled button is a recommendation, and there is no recommendation
    /// to make when the question is "shall I kill this process".
    /// </remarks>
    internal static ControlTemplate OutlineButtonTemplate(Brush ink)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Palette.Raised);
        border.SetValue(Border.BorderBrushProperty, Palette.Edge);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.PaddingProperty, Templated(nameof(Control.Padding)));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, ink, "bg"));
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Palette.RaisedHover, "bg"));
        template.Triggers.Add(hover);

        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, ink, "bg"));
        template.Triggers.Add(focused);

        return template;
    }

    /// <summary>The strip of key hints along the bottom of an overlay.</summary>
    internal static StackPanel HintBar(params StackPanel[] hints)
    {
        ArgumentNullException.ThrowIfNull(hints);

        var bar = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var hint in hints)
        {
            bar.Children.Add(hint);
        }

        return bar;
    }

    // ----------------------------- dialogs -----------------------------

    /// <summary>
    /// A dialog wearing the same clothes as the dashboard.
    /// </summary>
    /// <remarks>
    /// One factory because there are five of these, and five hand-built windows drifted apart
    /// exactly as you would expect: different paddings, different button chrome, and a title bar
    /// that was light on whichever one was written last. Anything opened from the dashboard
    /// should look like it came from the same application.
    /// </remarks>
    internal static Window Dialog(Window owner, string title, double width, double height)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            Owner = owner,
            Icon = TrayIcon.WindowIcon(),
            Background = Palette.Surface,
            Foreground = Palette.Text,
            FontFamily = new FontFamily("Segoe UI"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        window.SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(window).Handle);

        return window;
    }

    /// <summary>The heading inside a dialog, above whatever it is asking about.</summary>
    internal static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 19,
        FontWeight = FontWeights.SemiBold,
        Foreground = Palette.Text,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Explanatory text: smaller, quieter, and allowed to wrap.</summary>
    internal static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = Palette.Muted,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>
    /// The one button in a dialog that commits to something.
    /// </summary>
    /// <remarks>
    /// Filled rather than outlined, and there is never more than one: approving a plan, restoring
    /// a version and replacing an automation are all irreversible enough that the button doing
    /// them should not look like the button beside it that closes the window.
    /// </remarks>
    internal static Button Primary(string text, string glyph, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Glyph(glyph, 14, Palette.Surface));
        content.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
        });

        var button = new Button
        {
            Content = content,
            Foreground = Palette.Surface,
            Padding = new Thickness(16, 8, 18, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Template = FilledButtonTemplate(),
        };

        // See IconButton: panel content leaves the button nameless without this.
        System.Windows.Automation.AutomationProperties.SetName(button, text);

        button.Click += (_, _) => onClick();
        return button;
    }

    private static ControlTemplate FilledButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Palette.Accent);
        border.SetBinding(Border.PaddingProperty,
            new System.Windows.Data.Binding(nameof(Control.Padding))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88, "bg"));
        template.Triggers.Add(hover);

        // Pressing shrinks it a fraction. Tactile confirmation that the click landed, and the one
        // piece of motion here that is deliberately instant — a press you can watch arrive is a
        // press that felt slow.
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(
            UIElement.RenderTransformProperty, new ScaleTransform(0.97, 0.97), "bg"));
        pressed.Setters.Add(new Setter(
            UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5), "bg"));
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72, "bg"));
        template.Triggers.Add(pressed);

        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Edge, "bg"));
        template.Triggers.Add(off);

        return template;
    }

    /// <summary>The row of buttons along the bottom of a dialog.</summary>
    internal static StackPanel Buttons(params UIElement[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    /// <summary>Give a text box the same rounded, dark chrome as the search field.</summary>
    internal static TextBox Input(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        box.Background = Palette.Raised;
        box.Foreground = Palette.Text;
        box.CaretBrush = Palette.Accent;
        box.BorderBrush = Palette.Edge;
        box.BorderThickness = new Thickness(1);
        box.Padding = new Thickness(10, 8, 10, 8);
        box.FontSize = 13;
        box.Template = InputTemplate();
        return box;
    }

    private static ControlTemplate InputTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "shell");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, Templated(nameof(Control.Background)));
        border.SetBinding(Border.BorderBrushProperty, Templated(nameof(Control.BorderBrush)));
        border.SetBinding(Border.BorderThicknessProperty, Templated(nameof(Control.BorderThickness)));
        border.SetBinding(Border.PaddingProperty, Templated(nameof(Control.Padding)));

        // PART_ContentHost is the name the TextBox looks for; without it the box renders but
        // never shows a caret or accepts text.
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        host.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        border.AppendChild(host);

        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };

        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "shell"));
        template.Triggers.Add(focused);

        return template;
    }

    private static System.Windows.Data.Binding Templated(string property) =>
        new(property) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent };

    /// <summary>
    /// A block of plan or transcript text, in a card, scrolling on its own.
    /// </summary>
    /// <remarks>
    /// Monospaced because it is always JSON, a rendered plan or a timestamped log, and all three
    /// depend on their columns lining up to be readable at all.
    /// </remarks>
    internal static ScrollViewer CodePanel(string text, double minHeight = 0)
    {
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Palette.Raised,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            MinHeight = minHeight,
            Content = new TextBlock
            {
                Text = text,
                Foreground = Palette.Text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        scroller.Resources.Add(typeof(ScrollBar), SlimScrollBar());
        return scroller;
    }
}

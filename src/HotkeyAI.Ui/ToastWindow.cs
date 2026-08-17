using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Windows;

namespace HotkeyAI.Ui;

/// <summary>
/// The transient message behind <c>notify</c>.
/// </summary>
/// <remarks>
/// Unlike the other overlays this one never takes focus and never blocks. An automation that
/// announces what it did must not interrupt what the user is typing — a notification that steals
/// the keyboard is worse than no notification, because the keystrokes it swallows are gone.
/// </remarks>
internal sealed class ToastWindow : Window
{
    private const double Width_ = 380;

    private ToastWindow(string message, NotifyLevel level)
    {
        Width = Width_;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI");

        // The whole point of this window: appearing without stealing the foreground.
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        // Warning was the accent blue, which is the colour this app uses for "normal". Now that
        // the palette has a real amber, a warning looks like one.
        var (accent, glyph) = level switch
        {
            NotifyLevel.Error => (Palette.Danger, ""),
            NotifyLevel.Warning => (Palette.Warning, ""),
            _ => (Palette.Accent, ""),
        };

        var stripe = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0),
        };

        stripe.Children.Add(new Border
        {
            Width = 3,
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 11, 0),
        });

        // A glyph as well as the stripe. A three-pixel bar of colour is the whole difference
        // between "done" and "that failed", and colour alone is not a difference everyone can see.
        stripe.Children.Add(Fluent.Glyph(glyph, 15, accent));

        var text = new TextBlock
        {
            Text = message,
            Foreground = Palette.Text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };

        // A Grid, not a horizontal StackPanel. StackPanel measures along its orientation with
        // infinite space, so TextWrapping never engages and a long message is simply cut off at
        // the window edge — which is how the first version rendered. A star column gives the
        // text a real width to wrap inside.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(stripe, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(stripe);
        row.Children.Add(text);

        Content = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(16),
            Child = row,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };

        // Placed on SizeChanged, not SourceInitialized. This window sits against the bottom edge,
        // so its position depends on its own height — and with SizeToContent that height is still
        // zero when the handle is created. Positioning then puts the toast exactly one taskbar
        // height below the screen, where it is invisible but perfectly real.
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Show a toast and return immediately; it dismisses itself.</summary>
    internal static void Post(string message, NotifyLevel level)
    {
        var toast = new ToastWindow(message, level);
        toast.Show();

        var linger = level == NotifyLevel.Error ? 6.0 : 3.5;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(linger) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
            fade.Completed += (_, _) => toast.Close();
            toast.BeginAnimation(OpacityProperty, fade);
        };

        timer.Start();
    }

    /// <summary>Bottom-right of the monitor the user is currently working on.</summary>
    private void Place()
    {
        var area = Screens.WorkAreaFor(ForegroundWindow.Current());
        var source = PresentationSource.FromVisual(this);

        var scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        Left = ((area.Left + area.Width) * scaleX) - Width;
        Top = ((area.Top + area.Height) * scaleY) - ActualHeight - 8;
    }
}

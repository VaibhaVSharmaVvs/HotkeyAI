using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using HotkeyAI.Windows;

namespace HotkeyAI.Ui;

/// <summary>
/// Shared chrome and behaviour for every overlay the engine can put on screen.
/// </summary>
/// <remarks>
/// The parts that are easy to get wrong live here rather than in each window: coming to the
/// foreground at all, landing on the monitor the user is looking at, surviving display scaling,
/// and — the one that actually matters to an automation — giving focus back to whatever had it
/// when the overlay closes. A picker that leaves focus on a dead window turns the next
/// <c>type_text</c> into keystrokes sent nowhere.
/// </remarks>
public abstract class OverlayWindow : Window
{
    /// <summary>Fraction of the screen height the overlay's top edge sits at.</summary>
    /// <remarks>Slightly above centre: it reads as an overlay rather than a dialog.</remarks>
    private const double VerticalPosition = 0.22;

    private nint previousForeground;

    private bool hasBeenActivated;

    private bool isClosing;

    protected OverlayWindow(double width)
    {
        Width = width;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI");
        SnapsToDevicePixels = true;

        // Captured before the window exists, so it is genuinely the window the user was in.
        previousForeground = ForegroundWindow.Current();

        // Deeper radius and a heavier shadow than a dialog gets. An overlay has no title bar and
        // no frame, so the shadow is the only thing telling the eye it floats above the window
        // behind it — and being obviously in front is how it earns the second of attention it is
        // about to take.
        Card = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            Effect = new DropShadowEffect
            {
                BlurRadius = 40,
                ShadowDepth = 10,
                Direction = 270,
                Opacity = 0.6,
                Color = Colors.Black,
            },
            Margin = new Thickness(24),
        };

        Content = Card;

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => Place();

        // Clicking away dismisses. An overlay that survives losing focus is a trap: it is
        // borderless and has no close button, so there would be nothing to click.
        //
        // Only once it has genuinely been focused, though. Forcing the window forward can leave
        // it briefly inactive, and treating that as the user dismissing it would close the
        // picker before it ever appeared.
        Activated += (_, _) => hasBeenActivated = true;
        Deactivated += (_, _) =>
        {
            if (hasBeenActivated)
            {
                CancelIfOpen();
            }
        };
    }

    /// <summary>The rounded card that holds each window's content.</summary>
    protected Border Card { get; }

    /// <summary>Close the overlay as a cancellation. Idempotent.</summary>
    protected abstract void Cancel();

    /// <summary>
    /// Dismiss the overlay from outside — the panic key, or a cancelled run.
    /// </summary>
    /// <remarks>
    /// Must be called on the UI thread. Safety control 1 is worth little if the one thing on
    /// screen is a modal overlay that the panic key cannot close.
    /// </remarks>
    internal void Dismiss() => CancelIfOpen();


    /// <summary>
    /// Cancel, unless the overlay is already closing with an answer.
    /// </summary>
    /// <remarks>
    /// Load-bearing. Accepting a choice closes the window, closing it deactivates it, and
    /// deactivation is one of the ways the user cancels — so the naive wiring has every
    /// successful selection immediately overwrite itself with "cancelled" on the way out. It
    /// presents as a picker that looks right, highlights the right row, and reports that the user
    /// cancelled no matter what they press. The double-close guard alone does not help, because
    /// the result is discarded before the second close is ever attempted.
    /// </remarks>
    private void CancelIfOpen()
    {
        if (isClosing)
        {
            return;
        }

        Cancel();
    }

    /// <summary>
    /// Close once, whatever route got here.
    /// </summary>
    /// <remarks>
    /// Escape, clicking away, selecting an item and the panic key can all arrive nearly together;
    /// closing twice would raise on an already-closed window.
    /// </remarks>
    protected void CloseOnce()
    {
        if (isClosing)
        {
            return;
        }

        isClosing = true;
        Close();
    }

    /// <summary>
    /// Show the overlay modally and restore the caller's foreground window afterwards.
    /// </summary>
    /// <remarks>
    /// <c>ShowDialog</c> runs a nested dispatcher loop, so the UI thread keeps pumping while the
    /// engine waits. That is what lets a cancellation posted from the panic key close the window
    /// rather than deadlocking against it.
    /// </remarks>
    protected void ShowOverlay()
    {
        try
        {
            Show();
            Arrive();

            // WPF's Activate is subject to the same foreground lock as everything else, and the
            // agent has usually lost its claim by the time the overlay opens. Without this the
            // window appears but does not take keystrokes, which looks like a frozen picker.
            ForegroundWindow.Force(new WindowInteropHelper(this).Handle);
            Activate();
            Focus();

            var frame = new DispatcherFrameScope(this);
            frame.Run();
        }
        finally
        {
            RestoreForeground();
        }
    }

    /// <summary>
    /// Fade and lift the card into place.
    /// </summary>
    /// <remarks>
    /// The card only, not the window: an <c>AllowsTransparency</c> window animating its own
    /// opacity is composited on the CPU and stutters. Scaling from 0.97 rather than from nothing,
    /// because nothing appears from nothing — and only 3%, since this overlay is in the way of
    /// somebody's keystroke and has no business making them wait to see it.
    /// <para>
    /// Deliberately not applied to the toast, which is a separate window and never animates its
    /// arrival into the user's attention.
    /// </para>
    /// </remarks>
    private void Arrive()
    {
        var lift = new ScaleTransform(0.97, 0.97);
        Card.RenderTransformOrigin = new Point(0.5, 0.4);
        Card.RenderTransform = lift;
        Card.Opacity = 0;

        var grow = new DoubleAnimation(0.97, 1, Fluent.Motion.Enter)
        {
            EasingFunction = Fluent.Motion.Ease,
        };

        lift.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        lift.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        Card.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, Fluent.Motion.Snap) { EasingFunction = Fluent.Motion.Ease });
    }

    /// <summary>Hand the foreground back to whatever owned it before the overlay opened.</summary>
    private void RestoreForeground()
    {
        if (previousForeground == 0)
        {
            return;
        }

        var target = previousForeground;
        previousForeground = 0;
        ForegroundWindow.Force(target);
    }

    /// <summary>
    /// Position the overlay on the monitor that owned the foreground, in the right units.
    /// </summary>
    /// <remarks>
    /// Win32 reports monitor geometry in physical pixels and WPF positions windows in
    /// device-independent units, so the two only agree at 100% scaling. This machine runs at 125%,
    /// where skipping the conversion puts the overlay a fifth of the way off the intended spot —
    /// far enough to be obviously wrong on one monitor and off-screen on a second.
    /// </remarks>
    private void Place()
    {
        var area = Screens.WorkAreaFor(previousForeground);
        var source = PresentationSource.FromVisual(this);

        var scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        var left = area.Left * scaleX;
        var top = area.Top * scaleY;
        var width = area.Width * scaleX;
        var height = area.Height * scaleY;

        Left = left + ((width - Width) / 2);
        Top = top + (height * VerticalPosition);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelIfOpen();
        }
    }

    /// <summary>Runs a nested message loop until the window closes.</summary>
    /// <remarks>
    /// Used instead of <c>ShowDialog</c> because these windows have no owner: WPF's modal path
    /// wants one, and without it <c>ShowDialog</c> disables input on unrelated windows of the
    /// process rather than doing nothing.
    /// </remarks>
    private sealed class DispatcherFrameScope(Window window)
    {
        public void Run()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            void Done(object? sender, EventArgs e) => frame.Continue = false;

            window.Closed += Done;

            try
            {
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            finally
            {
                window.Closed -= Done;
            }
        }
    }
}

/// <summary>
/// The colour scheme, in one place so every window matches.
/// </summary>
/// <remarks>
/// Deep space charcoal, cool structural slate, electric cyan glow — 60/30/10. Most of the screen
/// is a neutral near-black that keeps a bright vector mark legible without competing with it;
/// cards and inputs sit one slate step above it; and cyan is spent only on the things that are
/// live or actionable.
/// <para>
/// One hue family across the surfaces, shifting lightness only, so stacking reads as depth rather
/// than as different materials. Text runs to four levels because two is not a hierarchy.
/// </para>
/// <para>
/// Three values are additions rather than substitutions, and each is here for a functional
/// reason: <see cref="Edge"/>, because the specified slate is a two-percent step against a card
/// and an invisible border is not a border; <see cref="Soft"/>, to fill the gap between primary
/// and muted text; and the three semantic colours, which no accent can carry — cyan cannot also
/// mean "broken". They are drawn from the same family so they read as belonging.
/// </para>
/// </remarks>
internal static class Palette
{
    // ---- 60%: deep space charcoal ----

    /// <summary>The application background and main layout canvas.</summary>
    public static SolidColorBrush Surface { get; } = Frozen(0xFF, 0x0B, 0x0E, 0x14);

    /// <summary>Window frames, and the card a row sits on.</summary>
    public static SolidColorBrush Raised { get; } = Frozen(0xFF, 0x16, 0x1B, 0x22);

    // ---- 30%: cool structural slate ----

    /// <summary>The same card under the pointer.</summary>
    public static SolidColorBrush RaisedHover { get; } = Frozen(0xFF, 0x21, 0x26, 0x2D);

    /// <summary>Input fields, keycaps, secondary button fills, a selected row.</summary>
    public static SolidColorBrush Selection { get; } = Frozen(0xFF, 0x21, 0x26, 0x2D);

    /// <summary>
    /// A boundary that should be findable but never the first thing seen.
    /// </summary>
    /// <remarks>
    /// One step above the specified slate. #21262D is the card fill, and a border in the same
    /// value as the surface it edges cannot be seen at all.
    /// </remarks>
    public static SolidColorBrush Edge { get; } = Frozen(0xFF, 0x30, 0x36, 0x3D);

    // ---- text: four levels ----

    /// <summary>Primary.</summary>
    public static SolidColorBrush Text { get; } = Frozen(0xFF, 0xF0, 0xF6, 0xFC);

    /// <summary>Secondary: supporting text still meant to be read.</summary>
    public static SolidColorBrush Soft { get; } = Frozen(0xFF, 0xC9, 0xD1, 0xD9);

    /// <summary>Tertiary: metadata, hints, counts.</summary>
    public static SolidColorBrush Muted { get; } = Frozen(0xFF, 0x8B, 0x94, 0x9E);

    /// <summary>Disabled: present, but not offering anything.</summary>
    public static SolidColorBrush Faint { get; } = Frozen(0xFF, 0x6E, 0x76, 0x81);

    // ---- 10%: electric cyan glow ----

    /// <summary>
    /// The accent: live keycaps, toggles, primary actions, execution indicators.
    /// </summary>
    /// <remarks>
    /// The deeper of the two cyans, because this one also has near-black text sitting on it in
    /// filled buttons and the brighter value pushes that toward glare.
    /// </remarks>
    public static SolidColorBrush Accent { get; } = Frozen(0xFF, 0x00, 0xD2, 0xFF);

    /// <summary>The brighter cyan, for the glow itself rather than for a surface.</summary>
    public static SolidColorBrush Glow { get; } = Frozen(0xFF, 0x00, 0xF2, 0xFE);

    /// <summary>
    /// Your verdict that an automation does what you meant.
    /// </summary>
    /// <remarks>
    /// Green rather than cyan, and the distinction is worth keeping: cyan is what the machine
    /// reports — live, running, actionable — and green is what <em>you</em> concluded. Painting
    /// both the same colour would merge a fact with an opinion.
    /// </remarks>
    public static SolidColorBrush Good { get; } = Frozen(0xFF, 0x3F, 0xB9, 0x50);

    /// <summary>
    /// On, but not running.
    /// </summary>
    /// <remarks>
    /// Its own colour rather than cyan or red, because it is neither. An automation switched on
    /// whose chord another application already holds is the failure this product is most prone
    /// to hiding: the user turned it on, the switch says on, and the key does nothing. Painting
    /// that cyan would make the dashboard lie in exactly the place it is meant to be trusted.
    /// </remarks>
    public static SolidColorBrush Warning { get; } = Frozen(0xFF, 0xD2, 0x99, 0x22);

    /// <summary>Off, failed, or about to destroy something.</summary>
    public static SolidColorBrush Danger { get; } = Frozen(0xFF, 0xF8, 0x51, 0x49);

    /// <summary>
    /// The glow of a backlight bleeding out from under a keycap.
    /// </summary>
    /// <remarks>
    /// Used behind a status dot so it reads as lit rather than painted, which is the one place
    /// this interface is allowed to look like the hardware it drives.
    /// </remarks>
    public static SolidColorBrush Backlight(SolidColorBrush of, double strength = 0.30)
    {
        ArgumentNullException.ThrowIfNull(of);

        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(strength * 255), of.Color.R, of.Color.G, of.Color.B));

        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

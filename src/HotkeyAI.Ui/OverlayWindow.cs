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
/// Taken from the product's actual world rather than from a dark-theme template. This app is
/// about a keyboard, and keyboard culture already has a mature palette tradition: keycap
/// colourways on an anodised case. So the neutrals are the <em>warm</em> dark grey of an anodised
/// aluminium body — never the blue-grey every dark app defaults to — the text is the warm
/// off-white of retro-beige legends, and the colours that mean something come from keycap sets:
/// botanical sage, clay ochre, terracotta, and a dusty periwinkle that holds hands with the blue
/// keycap in the app icon.
/// <para>
/// Distribution is roughly 60/30/10. <see cref="Surface"/> is most of the screen,
/// <see cref="Raised"/> and <see cref="Selection"/> carry the structure, and accent plus the
/// three semantic colours are the small remainder. Colour here is scarce on purpose: it means
/// status or action, and nothing on this screen is tinted for decoration.
/// </para>
/// <para>
/// One hue family across the surfaces, shifting lightness only — four steps of a few percent
/// each, so stacking reads as depth rather than as different materials.
/// </para>
/// </remarks>
internal static class Palette
{
    // ---- 60%: the case ----

    /// <summary>The window itself: warm near-black, the colour of an anodised body.</summary>
    public static SolidColorBrush Surface { get; } = Frozen(0xFF, 0x13, 0x12, 0x11);

    // ---- 30%: what sits on it ----

    /// <summary>A card, one step above the case.</summary>
    public static SolidColorBrush Raised { get; } = Frozen(0xFF, 0x1D, 0x1B, 0x19);

    /// <summary>The same card under the pointer.</summary>
    public static SolidColorBrush RaisedHover { get; } = Frozen(0xFF, 0x26, 0x23, 0x20);

    /// <summary>Inset surfaces — keycaps, inputs, a selected row. Darker, because they receive.</summary>
    public static SolidColorBrush Selection { get; } = Frozen(0xFF, 0x2E, 0x2A, 0x26);

    /// <summary>A boundary that should be findable but never the first thing seen.</summary>
    public static SolidColorBrush Edge { get; } = Frozen(0xFF, 0x33, 0x2F, 0x2A);

    // ---- text: four levels, because two is a flat hierarchy ----

    /// <summary>Primary: the warm off-white of a legend printed on a keycap.</summary>
    public static SolidColorBrush Text { get; } = Frozen(0xFF, 0xEF, 0xEA, 0xE2);

    /// <summary>Secondary: supporting text that is still meant to be read.</summary>
    public static SolidColorBrush Soft { get; } = Frozen(0xFF, 0xC4, 0xBD, 0xB2);

    /// <summary>Tertiary: metadata, hints, counts.</summary>
    public static SolidColorBrush Muted { get; } = Frozen(0xFF, 0x94, 0x8C, 0x80);

    /// <summary>Disabled: present, but not offering anything.</summary>
    public static SolidColorBrush Faint { get; } = Frozen(0xFF, 0x60, 0x5A, 0x52);

    // ---- 10%: colour that means something ----

    /// <summary>
    /// The one accent. Dusty periwinkle — a keycap blue, and a cool note against warm neutrals.
    /// </summary>
    public static SolidColorBrush Accent { get; } = Frozen(0xFF, 0x8F, 0xA9, 0xE8);

    /// <summary>A hotkey that is on and actually holding its combination. Botanical sage.</summary>
    public static SolidColorBrush Good { get; } = Frozen(0xFF, 0x93, 0xB4, 0x7F);

    /// <summary>
    /// On, but not running. Clay ochre.
    /// </summary>
    /// <remarks>
    /// Its own colour rather than green or red, because it is neither. An automation switched on
    /// whose chord another application already holds is the failure this product is most prone
    /// to hiding: the user turned it on, the switch says on, and the key does nothing. Painting
    /// that green would make the dashboard lie in exactly the place it is meant to be trusted.
    /// </remarks>
    public static SolidColorBrush Warning { get; } = Frozen(0xFF, 0xD9, 0xA0, 0x5B);

    /// <summary>Off, failed, or about to destroy something. Terracotta.</summary>
    public static SolidColorBrush Danger { get; } = Frozen(0xFF, 0xDD, 0x85, 0x71);

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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

        Card = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0),
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.5,
                Color = Colors.Black,
            },
            Margin = new Thickness(18),
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

/// <summary>The overlay colour scheme, in one place so the four windows match.</summary>
internal static class Palette
{
    public static SolidColorBrush Surface { get; } = Frozen(0xFF, 0x1E, 0x1E, 0x22);

    public static SolidColorBrush Edge { get; } = Frozen(0xFF, 0x3A, 0x3A, 0x42);

    public static SolidColorBrush Text { get; } = Frozen(0xFF, 0xEC, 0xEC, 0xF0);

    public static SolidColorBrush Muted { get; } = Frozen(0xFF, 0x92, 0x92, 0x9E);

    public static SolidColorBrush Accent { get; } = Frozen(0xFF, 0x5A, 0x9C, 0xF8);

    public static SolidColorBrush Selection { get; } = Frozen(0xFF, 0x2C, 0x3A, 0x52);

    public static SolidColorBrush Danger { get; } = Frozen(0xFF, 0xF2, 0x7A, 0x6E);

    /// <summary>A hotkey that is on and actually holding its combination.</summary>
    public static SolidColorBrush Good { get; } = Frozen(0xFF, 0x4C, 0xC3, 0x8A);

    /// <summary>
    /// On, but not running.
    /// </summary>
    /// <remarks>
    /// Its own colour rather than green or red, because it is neither. An automation switched on
    /// whose chord another application already holds is the failure this product is most prone
    /// to hiding: the user turned it on, the switch says on, and the key does nothing. Painting
    /// that green would make the dashboard lie in exactly the place it is meant to be trusted.
    /// </remarks>
    public static SolidColorBrush Warning { get; } = Frozen(0xFF, 0xE0, 0xA8, 0x4E);

    /// <summary>The card surface a row sits on, one step above the window.</summary>
    public static SolidColorBrush Raised { get; } = Frozen(0xFF, 0x2A, 0x2A, 0x30);

    /// <summary>The same card under the pointer.</summary>
    public static SolidColorBrush RaisedHover { get; } = Frozen(0xFF, 0x33, 0x33, 0x3B);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

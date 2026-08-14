using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HotkeyAI.Ui;

/// <summary>One entry in the tray menu.</summary>
/// <param name="Text">Label.</param>
/// <param name="OnClick">What it does. Null renders a caption rather than a command.</param>
/// <param name="Checked">Shows a tick in the icon column.</param>
/// <param name="Enabled">Greyed out when false.</param>
/// <param name="Glyph">
/// A Segoe Fluent Icons code point. Purely decorative — the label always says what the item does,
/// because icon fonts differ between Windows versions and a menu that reads as a row of empty
/// boxes must still be usable.
/// </param>
public sealed record TrayCommand(
    string Text,
    Action? OnClick = null,
    bool Checked = false,
    bool Enabled = true,
    string? Glyph = null)
{
    /// <summary>A separator line.</summary>
    public static TrayCommand Separator { get; } = new("-");
}

/// <summary>
/// The tray presence: the only part of the agent a user normally sees.
/// </summary>
/// <remarks>
/// This is what turns the agent from a console window somebody must remember not to close into
/// something that can just be running. That matters more than it sounds: every automation dies
/// silently the moment that console is closed, and nothing anywhere would explain why.
/// <para>
/// Runs on the shared <see cref="UiThread"/>, which already pumps messages for the overlays.
/// A tray icon needs a message loop, and giving it a second one would mean two UI threads in a
/// process whose whole job is to keep a third — the hotkey pump — responsive.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly Func<IReadOnlyList<TrayCommand>> buildMenu;

    /// <summary>Where failures go. A tray that quietly stops responding is unfixable.</summary>
    private static Action<string>? report;

    private TrayIcon(
        string tooltip, Func<IReadOnlyList<TrayCommand>> buildMenu, Action? onActivate)
    {
        this.buildMenu = buildMenu;

        icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = Truncate(tooltip),
        };

        // No ContextMenuStrip: the menu is WPF. See TrayMenu for why. Right-click is handled here
        // so the menu can be built fresh each time, from live state.
        icon.MouseUp += (_, e) => Guarded(() =>
        {
            switch (e.Button)
            {
                case MouseButtons.Right:
                    TrayMenu.Show(buildMenu());
                    break;

                // A single left click, not only a double click. On Windows 11 a new tray icon
                // starts in the overflow flyout, and the first click of a double click dismisses
                // that flyout — so the second never reaches the icon and double-clicking appears
                // to do nothing at all. A single click is the only gesture that works in both
                // places. Opening twice is harmless: the dashboard raises the existing window.
                case MouseButtons.Left:
                    onActivate?.Invoke();
                    break;

                default:
                    break;
            }
        });

    }

    /// <summary>Create the tray icon on the UI thread.</summary>
    /// <param name="tooltip">Hover text.</param>
    /// <param name="buildMenu">Called each time the menu opens, on the UI thread.</param>
    /// <param name="onActivate">The icon's default action, on a left click.</param>
    /// <param name="onError">Where to report a failed interaction.</param>
    public static Task<TrayIcon> ShowAsync(
        string tooltip,
        Func<IReadOnlyList<TrayCommand>> buildMenu,
        Action? onActivate = null,
        Action<string>? onError = null)
    {
        report = onError;
        return UiThread.Shared.InvokeAsync(() => new TrayIcon(tooltip, buildMenu, onActivate));
    }

    /// <summary>Update the hover text to reflect current state.</summary>
    public void SetTooltip(string tooltip) =>
        UiThread.Shared.Post(() => icon.Text = Truncate(tooltip));

    /// <summary>Show a balloon notification from the tray.</summary>
    public void Notify(string title, string message, bool isError = false) =>
        UiThread.Shared.Post(() =>
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = message;
            icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
            icon.ShowBalloonTip(isError ? 8000 : 4000);
        });

    /// <summary>
    /// Remove the icon, before the caller carries on shutting down.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose. Posting the removal and returning immediately is a race the icon
    /// loses: the process tears down before the message is pumped, and the user is left with a
    /// dead icon in the tray after clicking Quit — which reads as the app having failed to close.
    /// </remarks>
    public void Dispose() => UiThread.Shared.InvokeAsync(() =>
    {
        // Explicitly hidden first. A NotifyIcon disposed without this can leave a stale icon in
        // the tray until the user happens to move the mouse over it.
        icon.Visible = false;
        icon.Dispose();
        return true;
    }).GetAwaiter().GetResult();

    /// <summary>
    /// Run a tray interaction, reporting anything that goes wrong.
    /// </summary>
    /// <remarks>
    /// Without this, a click handler that throws leaves the icon looking alive and doing nothing
    /// — the failure mode this project has already been bitten by twice, where the only symptom
    /// is that the thing simply does not happen. An exception here also runs on the message loop,
    /// so left alone it would take the process, and every hotkey, down with it.
    /// </remarks>
    private static void Guarded(Action work)
    {
        try
        {
            work();
        }
#pragma warning disable CA1031 // Reported, not fatal: this runs on the message pump.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            report?.Invoke($"Tray interaction failed: {ex}");
        }
    }

    /// <summary>Tray tooltips are truncated by the shell at 127 characters.</summary>
    private static string Truncate(string text) =>
        text.Length <= 127 ? text : text[..124] + "...";

    /// <summary>
    /// Draw the tray icon rather than shipping an .ico.
    /// </summary>
    /// <remarks>
    /// Deliberately generated: a binary asset in the repository is one more thing that cannot be
    /// reviewed in a diff, and this is a keycap with a letter on it. It is drawn at 32x32 and
    /// left for the shell to scale.
    /// </remarks>
    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var body = new SolidBrush(Color.FromArgb(255, 90, 156, 248));
            using var path = Rounded(new Rectangle(2, 4, 28, 24), 6);
            g.FillPath(body, path);

            using var glyph = new SolidBrush(Color.FromArgb(255, 20, 22, 30));
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var centre = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            g.DrawString("K", font, glyph, new RectangleF(2, 4, 28, 24), centre);
        }

        // Icon.FromHandle does not own the underlying handle, so the icon is cloned and the
        // original destroyed. Skipping this leaks a GDI handle for the life of the process.
        var handle = bitmap.GetHicon();

        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            HotkeyAI.Windows.GdiHandle.DestroyIcon(handle);
        }
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }
}

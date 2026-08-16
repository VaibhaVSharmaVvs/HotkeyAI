using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HotkeyAI.Ui;

/// <summary>
/// Runs an automation and shows what it does, line by line, as it does it.
/// </summary>
/// <remarks>
/// The gap this closes is between pressing a hotkey and finding out whether it worked. Until now
/// that meant triggering the automation, watching windows move, and then opening a log file to
/// find out what the engine thought happened — by which point the desktop has moved on and the
/// interesting moment has passed.
/// <para>
/// Two things make it worth building rather than telling people to read the log. Steps appear as
/// they start, so a plan that hangs shows <em>which</em> action it is hanging on rather than
/// simply stopping. And the run ends on the two questions that matter — did that do what you
/// meant, and if not, what went wrong — so the verdict is recorded while the user still remembers
/// what they just watched.
/// </para>
/// <para>
/// It is a real run against the real desktop, which is why it is gated on approval and why the
/// window says so. A dry run would be safe and useless: the failures worth finding are the ones
/// that only happen against real windows.
/// </para>
/// </remarks>
internal sealed class TestRunWindow : Window, IDisposable
{
    private readonly IDashboardHost host;
    private readonly DashboardEntry entry;
    private readonly StackPanel lines = new();
    private readonly ScrollViewer scroller;
    private readonly TextBlock caption;
    private readonly Button stop;
    private readonly StackPanel verdicts;

    private readonly CancellationTokenSource cancellation = new();

    private string transcript = "";
    private bool finished;

    /// <summary>Set once this window starts closing, so a late step cannot touch it.</summary>
    private bool closing;

    private TestRunWindow(IDashboardHost host, DashboardEntry entry)
    {
        this.host = host;
        this.entry = entry;

        Title = $"Test run — {entry.Name}";
        Width = 780;
        Height = 560;
        Background = Palette.Surface;
        Foreground = Palette.Text;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        caption = new TextBlock
        {
            Text = "Starting…",
            Foreground = Palette.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        Grid.SetRow(caption, 0);
        layout.Children.Add(caption);

        scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Palette.Selection,
            Padding = new Thickness(8),
            Content = lines,
        };

        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        stop = Button("Stop", () => cancellation.Cancel(), Palette.Danger);

        // Hidden until the run ends. Asking "does this work?" while it is still running invites an
        // answer to a question the user cannot have seen the end of.
        verdicts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
        };

        verdicts.Children.Add(Button("Copy transcript", CopyTranscript, Palette.Text));
        verdicts.Children.Add(Button("It works", () => Judge(HealthState.Works), Palette.Accent));
        verdicts.Children.Add(
            Button("Not working", () => Judge(HealthState.NotWorking), Palette.Danger));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        buttons.Children.Add(Button("Close", Close, Palette.Text));
        buttons.Children.Add(verdicts);
        buttons.Children.Add(stop);

        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);

        Content = layout;

        // Closing the window stops the run rather than leaving it going invisibly. An automation
        // that carries on moving windows after the thing that started it has gone is the sort of
        // behaviour that makes people uninstall a tray app.
        Closing += (_, _) =>
        {
            closing = true;
            cancellation.Cancel();
        };

        SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// Releases the token source once the window is gone.
    /// </summary>
    /// <remarks>
    /// Called from <c>Closed</c>, not from <c>Closing</c>. The run is cancelled on Closing and
    /// takes a moment to unwind; disposing the source it is still using would replace a clean
    /// cancellation with an <see cref="ObjectDisposedException"/> from inside the executor.
    /// </remarks>
    public void Dispose() => cancellation.Dispose();

    /// <summary>
    /// Run an automation in front of the user.
    /// </summary>
    /// <returns>True if they recorded a verdict, so the caller knows to refresh.</returns>
    internal static bool Show(Window owner, IDashboardHost host, DashboardEntry entry)
    {
        var window = new TestRunWindow(host, entry) { Owner = owner };
        window.Closed += (_, _) => window.Dispose();

        // Started from Loaded rather than before ShowDialog, so the first steps have somewhere to
        // land. A plan whose first action is fast would otherwise report into a window that is not
        // on screen yet, and its opening lines would be lost.
        window.Loaded += (_, _) => window.Begin();

        return window.ShowDialog() == true;
    }

    private async void Begin()
    {
        // Progress marshals to this thread because it is constructed on it. The executor reports
        // from a worker, so without that every line would be a cross-thread access.
        var steps = new Progress<RunStep>(Add);

        TestRunResult result;

        try
        {
            result = await host.TestRunAsync(entry.FileName, steps, cancellation.Token)
                .ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The window must report a failure, not vanish with the agent.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = new TestRunResult(false, 0, ex.Message, "");
        }

        if (closing)
        {
            return;
        }

        Finish(result);
    }

    /// <summary>
    /// Render one step.
    /// </summary>
    /// <remarks>
    /// A <see cref="StepMood.Running"/> step is a caption, not a line. Actions nest — a
    /// <c>foreach</c> is announced before its body and logged after it — so treating "started" as
    /// a row to be replaced later would need a stack, and would still end up printing a parent's
    /// result above its children's. The caption sidesteps all of it: the list only ever holds
    /// finished steps, in exactly the order the transcript holds them, and the caption says what
    /// is happening now.
    /// </remarks>
    private void Add(RunStep step)
    {
        if (closing)
        {
            return;
        }

        // A "running" caption that arrives after the run has ended is always wrong, and it is
        // reachable: Progress posts each step to this thread, while the await that calls Finish
        // posts separately, so the last caption can land after the summary and overwrite it. The
        // first live run showed exactly that — a finished automation still claiming to be running
        // its last action, with the verdict buttons already up next to it.
        if (finished && step.Mood == StepMood.Running)
        {
            return;
        }

        if (step.Mood == StepMood.Running)
        {
            caption.Text = $"Running  ·  {step.Text}";
            caption.Foreground = Palette.Muted;
            return;
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var time = new TextBlock
        {
            Text = step.Time,
            Foreground = Palette.Muted,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 10, 0),
        };

        Grid.SetColumn(time, 0);
        row.Children.Add(time);

        var text = new TextBlock
        {
            Text = step.Text,
            Foreground = Ink(step.Mood),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        lines.Children.Add(new Border
        {
            Padding = new Thickness(4, 2, 4, 2),
            Child = row,
        });

        scroller.ScrollToEnd();
    }

    private void Finish(TestRunResult result)
    {
        finished = true;
        transcript = result.Transcript;

        stop.Visibility = Visibility.Collapsed;
        verdicts.Visibility = Visibility.Visible;

        if (cancellation.IsCancellationRequested)
        {
            caption.Text = "Stopped.";
            caption.Foreground = Palette.Danger;
        }
        else if (!result.Succeeded)
        {
            caption.Text = result.FailureReason ?? "It did not finish.";
            caption.Foreground = Palette.Danger;
        }
        else if (result.Unverified > 0)
        {
            // Said plainly, because this is the failure this product is most prone to: every
            // action reports success and nothing actually happened. The engine cannot tell, so
            // the person watching has to.
            caption.Text = result.Unverified == 1
                ? "Finished, but one action ran unverified — nothing confirmed it did anything. "
                  + "Did it do what you meant?"
                : $"Finished, but {result.Unverified} actions ran unverified — nothing confirmed "
                  + "they did anything. Did it do what you meant?";
            caption.Foreground = Palette.Text;
        }
        else
        {
            caption.Text = "Finished, and every step was verified. Did it do what you meant?";
            caption.Foreground = Palette.Accent;
        }
    }

    /// <summary>Record the verdict and close, so the answer is given once and in one place.</summary>
    private void Judge(HealthState state)
    {
        if (!finished)
        {
            return;
        }

        host.SetHealth(entry.FileName, state, null);
        DialogResult = true;
        Close();
    }

    private void CopyTranscript()
    {
        if (transcript.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(transcript);
            caption.Text = "Transcript copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process holds the clipboard. Worth saying rather than appearing to work.
            caption.Text = "Could not copy — something else is holding the clipboard.";
            caption.Foreground = Palette.Danger;
        }
    }

    private static SolidColorBrush Ink(StepMood mood) => mood switch
    {
        StepMood.Verified => Palette.Accent,
        StepMood.Failed => Palette.Danger,
        StepMood.Idle => Palette.Muted,

        // Unverified is deliberately not green. "It ran" and "it worked" are different claims, and
        // painting them the same colour is how a user comes to trust an automation that does
        // nothing.
        _ => Palette.Text,
    };

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
            Cursor = Cursors.Hand,
        };

        button.Click += (_, _) => onClick();
        return button;
    }
}

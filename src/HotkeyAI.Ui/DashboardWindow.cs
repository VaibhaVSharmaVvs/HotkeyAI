using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HotkeyAI.Ui;

/// <summary>
/// The management window: what is installed, what is running, and how to add more.
/// </summary>
/// <remarks>
/// Opened from the tray, and hosted in the agent's process because it needs live registration
/// state that only the agent has. It is an ordinary resizable window rather than an overlay: this
/// is somewhere the user reads and decides, not something that interrupts them mid-keystroke.
/// </remarks>
public sealed class DashboardWindow : Window
{
    private readonly IDashboardHost host;
    private readonly StackPanel list = new();
    private readonly TextBox description = Field(minLines: 3);
    private readonly TextBox hotkey = Field(minLines: 1);
    private readonly TextBox pasted = Field(minLines: 6);
    private readonly TextBlock status = new()
    {
        Foreground = Palette.Muted,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private CheckBox autostart = new();

    /// <summary>Set while the window writes to its own controls, to stop re-entry.</summary>
    private bool refreshing;

    private DashboardWindow(IDashboardHost host)
    {
        this.host = host;

        Title = "Hotkey AI";
        Width = 860;
        Height = 700;
        MinWidth = 640;
        MinHeight = 480;
        Background = Palette.Surface;
        Foreground = Palette.Text;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = Header();
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list,
            Margin = new Thickness(0, 12, 0, 12),
        };

        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        // Collapsed by default. The list is what this window is for; authoring is something you
        // do occasionally, and giving it a third of the height permanently would leave eight
        // automations sharing a box three rows tall.
        var authoring = new Expander
        {
            Header = "New automation",
            Foreground = Palette.Text,
            IsExpanded = false,
            Content = Authoring(),
        };

        Grid.SetRow(authoring, 2);
        layout.Children.Add(authoring);

        Content = layout;
        Loaded += (_, _) => Refresh();

        SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    /// <summary>Show the dashboard, or bring the existing one forward.</summary>
    public static void Open(IDashboardHost host) => UiThread.Shared.Post(() =>
    {
        // One window, not one per click on the tray menu. Two dashboards would show two views of
        // the same state and disagree the moment either was used.
        var existing = Application.Current?.Windows.OfType<DashboardWindow>().FirstOrDefault()
            ?? OpenWindows.FirstOrDefault();

        if (existing is not null)
        {
            existing.Refresh();
            existing.Activate();
            HotkeyAI.Windows.ForegroundWindow.Force(
                new System.Windows.Interop.WindowInteropHelper(existing).Handle);
            return;
        }

        var window = new DashboardWindow(host);
        OpenWindows.Add(window);
        window.Closed += (_, _) => OpenWindows.Remove(window);
        window.Show();

        // Same foreground lock as everywhere else: the agent has usually lost its claim by now.
        HotkeyAI.Windows.ForegroundWindow.Force(
            new System.Windows.Interop.WindowInteropHelper(window).Handle);
        window.Activate();
    });

    /// <summary>
    /// Tracks the open dashboard.
    /// </summary>
    /// <remarks>
    /// The agent has no <c>Application</c> instance — the overlays never needed one — so
    /// <c>Application.Current.Windows</c> is null here and cannot be used to find it.
    /// </remarks>
    private static readonly List<DashboardWindow> OpenWindows = [];

    private void Refresh()
    {
        list.Children.Clear();

        var entries = host.Load();

        if (entries.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No automations yet. Describe one below to get started.",
                Foreground = Palette.Muted,
                Margin = new Thickness(4, 12, 4, 12),
            });
        }

        foreach (var entry in entries)
        {
            list.Children.Add(Row(entry));
        }

        refreshing = true;
        autostart.IsChecked = host.AutostartEnabled;
        refreshing = false;
    }

    private Grid Header()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Automations",
            FontSize = 20,
            Foreground = Palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(title, 0);
        row.Children.Add(title);

        autostart = new CheckBox
        {
            Content = "Start at login",
            Foreground = Palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        void AutostartChanged(object sender, RoutedEventArgs e)
        {
            if (refreshing)
            {
                return;
            }

            host.AutostartEnabled = autostart.IsChecked == true;

            // Read back rather than trusting the tick: enabling can fail, and a checkbox that
            // stays on after a failed write is a lie about whether this starts at login.
            refreshing = true;
            autostart.IsChecked = host.AutostartEnabled;
            refreshing = false;

            Say(autostart.IsChecked == true
                ? "Hotkey AI will start when you sign in."
                : "Hotkey AI will not start automatically.");
        }

        autostart.Checked += AutostartChanged;
        autostart.Unchecked += AutostartChanged;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(autostart);
        buttons.Children.Add(Button("Reload", () => { host.Reload(); Refresh(); Say("Reloaded."); }));
        buttons.Children.Add(Button("Folder", host.OpenAutomationsFolder));
        buttons.Children.Add(Button("Log", host.OpenLog));

        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        return row;
    }

    private Border Row(DashboardEntry entry)
    {
        var card = new Border
        {
            Background = Palette.Selection,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // A dot rather than a word: whether something is live is the one thing the user scans for.
        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = entry.IsLive ? Palette.Accent : Palette.Muted,
        };

        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = entry.Name,
            Foreground = Palette.Text,
            FontSize = 14,
        });

        text.Children.Add(new TextBlock
        {
            Text = $"{entry.Chord}   ·   {entry.State}",
            Foreground = entry.IsLive ? Palette.Muted : Palette.Danger,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (entry.NeedsApproval)
        {
            actions.Children.Add(Button("Review", () => Review(entry)));
        }

        var toggle = new CheckBox
        {
            IsChecked = entry.IsEnabled,
            Content = "On",
            Foreground = Palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        // Checked/Unchecked rather than Click. Click only fires for mouse and keyboard, so a
        // toggle driven through UI Automation — which is how a screen reader operates it — would
        // move the tick and change nothing, leaving the dashboard claiming an automation was off
        // while its hotkey stayed live. IsChecked is set above, before these are attached, so
        // building the row cannot re-enter.
        toggle.Checked += (_, _) => Toggle(entry, enabled: true);
        toggle.Unchecked += (_, _) => Toggle(entry, enabled: false);

        actions.Children.Add(toggle);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private void Toggle(DashboardEntry entry, bool enabled)
    {
        if (refreshing)
        {
            return;
        }

        host.SetEnabled(entry.FileName, enabled);
        Refresh();
        Say($"{entry.Name} is {(enabled ? "on" : "off")}.");
    }

    /// <summary>
    /// Show the plan, then offer to approve it.
    /// </summary>
    /// <remarks>
    /// The rendered plan is always shown first, and the button says what it means. Approval is
    /// the control that stops a dropped file running on a keypress; a yes/no on a file name would
    /// make it a formality.
    /// </remarks>
    private void Review(DashboardEntry entry)
    {
        var window = new Window
        {
            Title = $"Review {entry.Name}",
            Width = 720,
            Height = 620,
            Background = Palette.Surface,
            Foreground = Palette.Text,
            FontFamily = new FontFamily("Segoe UI"),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = entry.Preview,
                Foreground = Palette.Text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        Grid.SetRow(body, 0);
        layout.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        buttons.Children.Add(Button("Close", window.Close));
        buttons.Children.Add(Button("I have read this — approve", () =>
        {
            host.Approve(entry.FileName);
            window.Close();
            Refresh();
            Say($"{entry.Name} approved.");
        }));

        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);

        window.SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(window).Handle);

        window.Content = layout;
        window.ShowDialog();
    }

    private StackPanel Authoring()
    {
        var panel = new StackPanel();

        panel.Children.Add(Label("Describe what it should do"));
        panel.Children.Add(description);

        panel.Children.Add(Label("Hotkey (optional, e.g. CTRL+ALT+J)"));
        panel.Children.Add(hotkey);

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        top.Children.Add(Button("Copy prompt for Claude Code", CopyPrompt));
        panel.Children.Add(top);

        panel.Children.Add(Label("Paste the JSON it gives you"));
        panel.Children.Add(pasted);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(Button("Check", CheckPasted));
        bottom.Children.Add(Button("Preview", PreviewPasted));
        bottom.Children.Add(Button("Save", SavePasted));
        panel.Children.Add(bottom);

        panel.Children.Add(status);
        return panel;
    }

    private void CopyPrompt()
    {
        var prompt = host.BuildAuthoringPrompt(description.Text, hotkey.Text);

        try
        {
            Clipboard.SetText(prompt);
            Say("Prompt copied. Paste it into Claude Code in the Hotkey AI repository, then bring "
                + "the JSON back here.");
        }
#pragma warning disable CA1031 // The clipboard is genuinely flaky; another app can hold it open.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Say($"Could not copy to the clipboard: {ex.Message}");
        }
    }

    private void CheckPasted()
    {
        var problems = host.ValidatePlan(pasted.Text);

        Say(problems.Count == 0
            ? "Valid. Preview it to check it says what you meant."
            : string.Join(Environment.NewLine, problems.Take(5)));
    }

    private void PreviewPasted() => Say(host.ExplainPlan(pasted.Text));

    private void SavePasted()
    {
        var error = host.SavePlan(pasted.Text);

        if (error is not null)
        {
            Say(error);
            return;
        }

        pasted.Clear();
        description.Clear();
        hotkey.Clear();
        Refresh();
        Say("Saved. It is switched on but still needs approval — press Review to read it.");
    }

    private void Say(string message) => status.Text = message;

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Palette.Muted,
        FontSize = 11,
        Margin = new Thickness(0, 8, 0, 4),
    };

    private static TextBox Field(int minLines) => new()
    {
        Background = Palette.Selection,
        Foreground = Palette.Text,
        CaretBrush = Palette.Accent,
        BorderBrush = Palette.Edge,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 6, 8, 6),
        FontSize = 13,
        AcceptsReturn = minLines > 1,
        TextWrapping = minLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MinLines = minLines,
        MaxLines = minLines > 1 ? minLines * 2 : 1,
        VerticalScrollBarVisibility = minLines > 1 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
    };

    private static Button Button(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Foreground = Palette.Text,
            Background = Palette.Edge,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        button.Click += (_, _) => onClick();
        return button;
    }
}

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
        Icon = TrayIcon.WindowIcon();
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

        var detail = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 0),
        };

        // The chord is the control that changes it. Putting rebinding behind a separate button
        // labelled something else would leave the most obvious thing on the row inert.
        var rebind = new Button
        {
            Content = entry.Chord,
            Foreground = Palette.Text,
            Background = Palette.Edge,
            BorderBrush = Palette.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Change this hotkey",
        };

        rebind.Click += (_, _) =>
        {
            if (HotkeyCaptureWindow.Show(this, host, entry.FileName, entry.Name, entry.Chord))
            {
                Refresh();
                Say($"{entry.Name} rebound.");
            }
        };

        detail.Children.Add(rebind);

        detail.Children.Add(new TextBlock
        {
            Text = entry.Health switch
            {
                HealthState.Works => "  ✓ works",
                HealthState.NotWorking => "  ✗ not working",
                _ => "  · not tested",
            },
            Foreground = entry.Health switch
            {
                HealthState.Works => Palette.Accent,
                HealthState.NotWorking => Palette.Danger,
                _ => Palette.Muted,
            },
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = entry.HealthNote,
        });

        detail.Children.Add(new TextBlock
        {
            Text = entry.LastRun is null ? entry.State : $"{entry.State}   ·   {entry.LastRun}",
            Foreground = entry.IsLive ? Palette.Muted : Palette.Danger,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(detail);

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

        // Watching it run is how the verdict below gets answered honestly, so it sits next to
        // them rather than somewhere else on the row.
        //
        // Absent rather than disabled when the plan cannot run, because the reason is always
        // something the row already says and usually the Review button next to it. An offered
        // button that refuses would have to explain itself in the status line, which on a full
        // list is scrolled out of sight — a refusal nobody reads is a button that does nothing.
        if (entry.CanTestRun)
        {
            actions.Children.Add(Button("Test run", () => TestRun(entry)));
        }

        // The two halves of "does this actually do what I meant?". Clicking the verdict an
        // automation already has withdraws it, so a wrong click is one click to undo.
        actions.Children.Add(Verdict("Works", entry, HealthState.Works));
        actions.Children.Add(Verdict("Not working", entry, HealthState.NotWorking));

        // Only once it has actually run. Offering repair for an automation with no transcript
        // would produce a prompt whose most useful section says "there is no transcript".
        if (entry.LastRun is not null)
        {
            actions.Children.Add(Button("Repair", () => Repair(entry)));
        }

        actions.Children.Add(Button("History", () => History(entry)));

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

    /// <summary>
    /// Run an automation in front of the user, and record what they make of it.
    /// </summary>
    /// <remarks>
    /// Refused up front when it cannot run, rather than opening a window that immediately reports
    /// a refusal. The commonest reason is that the plan has not been approved yet, and the useful
    /// response to that is the Review button two inches away — not a dialog.
    /// </remarks>
    private void TestRun(DashboardEntry entry)
    {
        if (host.WhyNotTestable(entry.FileName) is { } refusal)
        {
            Say($"{entry.Name}: {refusal}");
            return;
        }

        if (TestRunWindow.Show(this, host, entry))
        {
            Refresh();
            Say($"{entry.Name} — verdict recorded.");
        }
        else
        {
            // Refreshed either way. The run itself changes the row, because "last run" is on it.
            Refresh();
        }
    }

    /// <summary>
    /// One of the two verdict buttons, highlighted when it is the current verdict.
    /// </summary>
    /// <remarks>
    /// Marking an automation as not working goes straight on to the repair dialog. That is the
    /// whole point of recording the verdict: the moment a user decides something is broken is the
    /// moment they know what is wrong with it, and asking them again later gets a vaguer answer.
    /// </remarks>
    private Button Verdict(string text, DashboardEntry entry, HealthState state)
    {
        var active = entry.Health == state;

        var button = new Button
        {
            Content = text,
            Foreground = active
                ? (state == HealthState.Works ? Palette.Accent : Palette.Danger)
                : Palette.Muted,
            Background = active ? Palette.Selection : Palette.Edge,
            BorderBrush = active
                ? (state == HealthState.Works ? Palette.Accent : Palette.Danger)
                : Palette.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        button.Click += (_, _) =>
        {
            // Clicking the current verdict withdraws it rather than reasserting it.
            var next = active ? HealthState.Untested : state;
            host.SetHealth(entry.FileName, next, next == state ? entry.HealthNote : null);
            Refresh();

            if (next == HealthState.NotWorking)
            {
                Repair(entry with { Health = next });
            }
            else
            {
                Say(next switch
                {
                    HealthState.Works => $"{entry.Name} marked as working.",
                    _ => $"{entry.Name} is untested again.",
                });
            }
        };

        return button;
    }

    /// <summary>
    /// Past versions of a plan, with a diff against what is on disk now.
    /// </summary>
    /// <remarks>
    /// Restoring is the undo for an AI-authored change, which is the only reason it is safe to
    /// accept one. The diff is shown first because a version list on its own tells you when
    /// something changed and never what.
    /// </remarks>
    private void History(DashboardEntry entry)
    {
        var history = host.History(entry.FileName);

        if (history.Count == 0)
        {
            Say($"No history for {entry.Name} yet — versions are kept from the first time the "
                + "agent sees a change.");
            return;
        }

        var window = new Window
        {
            Title = $"History of {entry.Name}",
            Width = 520,
            Height = 460,
            Background = Palette.Surface,
            Foreground = Palette.Text,
            FontFamily = new FontFamily("Segoe UI"),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var list = new StackPanel();

        foreach (var version in history)
        {
            var row = new Button
            {
                Content = version.IsCurrent ? $"{version.Summary}   ·   on disk now" : version.Summary,
                Foreground = version.IsCurrent ? Palette.Muted : Palette.Text,
                Background = Palette.Selection,
                BorderBrush = Palette.Edge,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !version.IsCurrent,
            };

            var id = version.Id;

            row.Click += (_, _) =>
            {
                var older = host.ReadVersion(entry.FileName, id);
                var current = host.ReadCurrent(entry.FileName);

                if (older is null || current is null)
                {
                    Say("That version is no longer stored.");
                    return;
                }

                // Old on the left, current on the right: the diff reads as "what restoring would
                // undo", which is the question being asked.
                if (DiffWindow.Show(
                        window,
                        $"Restore {entry.Name}",
                        current,
                        older,
                        "Restore this version",
                        () => host.RestoreVersion(entry.FileName, id)))
                {
                    window.Close();
                    Refresh();
                    Say($"{entry.Name} restored. It needs approving again before it can run.");
                }
            };

            list.Children.Add(row);
        }

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var caption = Label("Pick a version to see what restoring it would change.");
        Grid.SetRow(caption, 0);
        layout.Children.Add(caption);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
            Margin = new Thickness(0, 8, 0, 0),
        };

        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        window.SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(window).Handle);

        window.Content = layout;
        window.ShowDialog();
    }

    /// <summary>
    /// Ask what went wrong, then hand over everything needed to fix it.
    /// </summary>
    /// <remarks>
    /// The transcript is shown, not just attached. Half the time it answers the question on its
    /// own — an action reported as unverified, or a step that failed because an application was
    /// not running, is often the whole story, and reading it beats pasting it.
    /// </remarks>
    private void Repair(DashboardEntry entry)
    {
        var run = host.LastRun(entry.FileName);

        var window = new Window
        {
            Title = $"Repair {entry.Name}",
            Width = 760,
            Height = 620,
            Background = Palette.Surface,
            Foreground = Palette.Text,
            FontFamily = new FontFamily("Segoe UI"),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var caption = Label("What did it do, and what should it have done?");
        Grid.SetRow(caption, 0);
        layout.Children.Add(caption);

        var complaint = Field(minLines: 3);
        complaint.Text = entry.HealthNote ?? "";
        Grid.SetRow(complaint, 1);
        layout.Children.Add(complaint);

        var body = new StackPanel();

        body.Children.Add(new TextBlock
        {
            Text = run is null ? "It has not run yet." : "The last run:",
            Foreground = Palette.Muted,
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 4),
        });

        body.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8),
            MaxHeight = 260,
            Content = new TextBlock
            {
                Text = run?.Transcript ?? "",
                Foreground = Palette.Muted,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            },
        });

        Grid.SetRow(body, 2);
        layout.Children.Add(body);

        var said = new TextBlock
        {
            Foreground = Palette.Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        buttons.Children.Add(Button("Close", window.Close));
        buttons.Children.Add(Button("Copy repair prompt", () =>
        {
            try
            {
                Clipboard.SetText(host.BuildRepairPrompt(entry.FileName, complaint.Text));

                // Keep what they wrote against the automation. It is the same sentence they would
                // otherwise have to remember and retype the next time they look at this row.
                if (entry.Health == HealthState.NotWorking && complaint.Text.Trim().Length > 0)
                {
                    host.SetHealth(entry.FileName, HealthState.NotWorking, complaint.Text.Trim());
                    Refresh();
                }

                said.Text = "Copied. Paste it into Claude Code in the Hotkey AI repository, then "
                    + "bring the corrected JSON back to New automation below.";
            }
#pragma warning disable CA1031 // The clipboard is genuinely flaky; another app can hold it open.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                said.Text = $"Could not copy to the clipboard: {ex.Message}";
            }
        }));

        var footer = new StackPanel();
        footer.Children.Add(buttons);
        footer.Children.Add(said);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);

        window.SourceInitialized += (_, _) => HotkeyAI.Windows.WindowTheme.UseDarkTitleBar(
            new System.Windows.Interop.WindowInteropHelper(window).Handle);

        window.Content = layout;
        window.Loaded += (_, _) => complaint.Focus();
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
        // A repaired plan comes back with the same name as the thing it repairs. Refusing it — as
        // this did — left the repair loop with no last step: you had the fix and nowhere to put
        // it. Now the collision is the interesting case, and the diff is what makes accepting it
        // a review rather than a leap.
        if (host.ExistingFileFor(pasted.Text) is { } existing)
        {
            var current = host.ReadCurrent(existing);

            if (current is not null
                && DiffWindow.Show(
                    this,
                    $"Replace {existing}",
                    current,
                    pasted.Text,
                    "Replace it",
                    () => host.ReplacePlan(existing, pasted.Text)))
            {
                pasted.Clear();
                description.Clear();
                hotkey.Clear();
                Refresh();
                Say($"{existing} replaced. It needs approving again before it can run.");
            }

            return;
        }

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

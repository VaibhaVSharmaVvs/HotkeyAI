using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HotkeyAI.Core.Matching;

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

    private readonly TextBox search = new();

    /// <summary>
    /// Which rows are open, by file name.
    /// </summary>
    /// <remarks>
    /// Kept on the window rather than on the row, because every refresh rebuilds the list from
    /// scratch. Without this, approving an automation — or a run finishing — would snap every
    /// open row shut underneath whoever was reading it.
    /// </remarks>
    private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);

    private ToggleButton autostart = new();

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
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = Header();
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list,
            Margin = new Thickness(0, 4, 0, 8),
        };

        // WPF's stock scrollbar is a light-theme control with arrow buttons at both ends, and it
        // is the one piece of chrome in this window that cannot be recoloured through properties.
        scroller.Resources.Add(typeof(ScrollBar), Fluent.SlimScrollBar());

        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        // Its own row, above the authoring panel rather than inside it. Everything this window
        // says back to you arrived here — and while it lived inside a collapsed expander at the
        // bottom of a full list, every one of those messages was scrolled out of sight.
        Grid.SetRow(status, 2);
        layout.Children.Add(status);

        // Collapsed by default. The list is what this window is for; authoring is something you
        // do occasionally, and giving it a third of the height permanently would leave eight
        // automations sharing a box three rows tall.
        var authoring = new Expander
        {
            Header = "New hotkey",
            Foreground = Palette.Text,
            FontSize = 13,
            IsExpanded = false,
            Content = Authoring(),
            Template = Fluent.ExpanderTemplate(Fluent.Add),
        };

        Grid.SetRow(authoring, 3);
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
        var query = search.Text;

        var shown = entries
            .Where(e => HotkeySearch.Matches(e.Name, e.Chord, query))
            .ToList();

        if (entries.Count == 0)
        {
            list.Children.Add(Empty("No hotkeys yet. Add one below to get started."));
        }
        else if (shown.Count == 0)
        {
            // Said differently from "none exist", because the two look identical on screen and
            // mean completely different things.
            list.Children.Add(Empty($"Nothing matches “{query.Trim()}”."));
        }

        foreach (var entry in shown)
        {
            list.Children.Add(Row(entry));
        }

        refreshing = true;
        autostart.IsChecked = host.AutostartEnabled;
        refreshing = false;
    }

    private static TextBlock Empty(string message) => new()
    {
        Text = message,
        Foreground = Palette.Muted,
        FontSize = 13,
        Margin = new Thickness(4, 18, 4, 18),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private StackPanel Header()
    {
        var stack = new StackPanel();

        // Title and search on one line, the way a settings window opens: what this is, and the
        // fastest way to reach one thing in it.
        var top = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "Hotkeys",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(title, 0);
        top.Children.Add(title);

        var field = Fluent.SearchBox(search, "Search by name or combination");
        field.Margin = new Thickness(24, 0, 0, 0);
        field.MaxWidth = 340;
        field.HorizontalAlignment = HorizontalAlignment.Right;

        // Filters as you type. A search that waits for Enter is a search people stop using.
        search.TextChanged += (_, _) =>
        {
            if (!refreshing)
            {
                Refresh();
            }
        };

        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && search.Text.Length > 0)
            {
                search.Clear();
                e.Handled = true;
            }
        };

        Grid.SetColumn(field, 1);
        top.Children.Add(field);
        stack.Children.Add(top);

        // Toolbar: the three things you do to the whole list, and the one setting that belongs
        // to the app rather than to any hotkey.
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Fluent.IconButton(
            Fluent.Refresh, "Reload", () => { host.Reload(); Refresh(); Say("Reloaded."); },
            tooltip: "Re-read the folder and rebind every hotkey"));
        actions.Children.Add(Fluent.IconButton(
            Fluent.Folder, "Folder", host.OpenAutomationsFolder,
            tooltip: "Open the automations folder"));
        actions.Children.Add(Fluent.IconButton(
            Fluent.Document, "Log", host.OpenLog, tooltip: "Open today's log"));

        Grid.SetColumn(actions, 0);
        bar.Children.Add(actions);

        autostart = Fluent.Switch(false, OnAutostart, "Start Hotkey AI when you sign in");

        var login = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        login.Children.Add(new TextBlock
        {
            Text = "Start at login",
            Foreground = Palette.Muted,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });

        login.Children.Add(autostart);

        Grid.SetColumn(login, 1);
        bar.Children.Add(login);
        stack.Children.Add(bar);

        return stack;
    }

    private void OnAutostart(bool wanted)
    {
        if (refreshing)
        {
            return;
        }

        host.AutostartEnabled = wanted;

        // Read back rather than trusting the switch: enabling can fail, and a control that stays
        // on after a failed write is a lie about whether this starts at login.
        refreshing = true;
        autostart.IsChecked = host.AutostartEnabled;
        refreshing = false;

        Say(autostart.IsChecked == true
            ? "Hotkey AI will start when you sign in."
            : "Hotkey AI will not start automatically.");
    }

    /// <summary>
    /// One hotkey: a summary you can scan, and everything else behind a chevron.
    /// </summary>
    /// <remarks>
    /// Collapsed, a row answers only the questions worth asking about every hotkey at once — is
    /// it on, what fires it, does it work. Ten rows of buttons is not a list you scan, it is a
    /// wall you read, and the buttons that matter are always the ones belonging to the single
    /// automation you came here about. Everything else is one click away.
    /// </remarks>
    private Border Row(DashboardEntry entry)
    {
        var open = expanded.Contains(entry.FileName);

        var body = new StackPanel();
        var card = Fluent.Card(body);
        card.Margin = new Thickness(0, 0, 0, 6);

        var head = new Grid { Margin = new Thickness(14, 11, 12, 11) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Green when it is on and holding its combination, amber when it is on and not, red when
        // it is off. Amber is the one that earns its keep: "switched on, but another application
        // owns this chord" is exactly the state a green dot would hide, and it is the failure
        // this product is most prone to hiding.
        var (colour, why) = entry switch
        {
            { IsEnabled: false } => (Palette.Danger, "Off"),
            { IsLive: true } => (Palette.Good, "On, and running"),
            _ => (Palette.Warning, $"On, but not running — {entry.State}"),
        };

        var dot = Fluent.Dot(colour);
        dot.Margin = new Thickness(0, 0, 12, 0);
        dot.ToolTip = why;
        Grid.SetColumn(dot, 0);
        head.Children.Add(dot);

        var naming = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        naming.Children.Add(new TextBlock
        {
            Text = entry.Name,
            Foreground = Palette.Text,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        naming.Children.Add(Keycap(entry));
        Grid.SetColumn(naming, 1);
        head.Children.Add(naming);

        // The verdict as one character. A row nobody has judged yet shows nothing rather than a
        // third symbol, because "untested" is the absence of an answer, not another answer.
        if (entry.Health != HealthState.Untested)
        {
            var works = entry.Health == HealthState.Works;
            var verdict = Fluent.Glyph(
                works ? Fluent.Tick : Fluent.Cross, 13,
                works ? Palette.Good : Palette.Danger);

            verdict.Margin = new Thickness(10, 0, 6, 0);
            verdict.ToolTip = entry.HealthNote is { Length: > 0 } note
                ? $"{(works ? "Works" : "Not working")} — {note}"
                : works ? "You marked this as working" : "You marked this as not working";

            Grid.SetColumn(verdict, 2);
            head.Children.Add(verdict);
        }

        var toggle = Fluent.Switch(
            entry.IsEnabled,
            on => Toggle(entry, on),
            entry.IsEnabled
                ? "On — pressing the combination runs it"
                : "Off — the combination does nothing");

        toggle.Margin = new Thickness(8, 0, 4, 0);
        Grid.SetColumn(toggle, 3);
        head.Children.Add(toggle);

        var chevron = Fluent.Glyph(Fluent.ChevronDown, 12, Palette.Muted);
        chevron.Margin = new Thickness(10, 0, 2, 0);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(open ? 180 : 0);
        Grid.SetColumn(chevron, 4);
        head.Children.Add(chevron);

        var header = new Border
        {
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = head,
        };

        header.MouseEnter += (_, _) => card.Background = Palette.RaisedHover;
        header.MouseLeave += (_, _) => card.Background = Palette.Raised;

        // The whole strip opens the row. A chevron you have to hit exactly is a target the size
        // of a full stop. The switch and the keycap are buttons, and buttons mark their own
        // clicks handled, so they still do their own jobs rather than expanding the row.
        header.MouseLeftButtonUp += (_, _) => SetExpanded(entry.FileName, !open);

        System.Windows.Automation.AutomationProperties.SetName(
            header, $"{entry.Name}, {entry.Chord}. {why}.");

        body.Children.Add(header);

        if (open)
        {
            body.Children.Add(Details(entry));
        }

        return card;
    }

    private void SetExpanded(string fileName, bool open)
    {
        if (open)
        {
            expanded.Add(fileName);
        }
        else
        {
            expanded.Remove(fileName);
        }

        Refresh();
    }

    /// <summary>
    /// The combination, drawn as the key it is — and the button that rebinds it.
    /// </summary>
    /// <remarks>
    /// The chord is the control that changes it. Putting rebinding behind a separate button
    /// labelled something else would leave the most obvious thing on the row inert.
    /// </remarks>
    private Button Keycap(DashboardEntry entry)
    {
        var cap = new Button
        {
            Content = new TextBlock
            {
                Text = entry.Chord,
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Palette.Text,
            },
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Change this combination",
            Template = KeycapTemplate(),
        };

        cap.Click += (_, e) =>
        {
            // Handled, so pressing the keycap rebinds instead of also opening the row.
            e.Handled = true;

            if (HotkeyCaptureWindow.Show(this, host, entry.FileName, entry.Name, entry.Chord))
            {
                Refresh();
                Say($"{entry.Name} rebound.");
            }
        };

        return cap;
    }

    private static ControlTemplate KeycapTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "cap");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, Palette.Selection);
        border.SetValue(Border.BorderBrushProperty, Palette.Edge);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.PaddingProperty,
            new System.Windows.Data.Binding(nameof(Control.Padding))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });

        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "cap"));
        template.Triggers.Add(hover);

        return template;
    }

    /// <summary>Everything about one hotkey that is not worth showing for all of them.</summary>
    private StackPanel Details(DashboardEntry entry)
    {
        var panel = new StackPanel { Margin = new Thickness(35, 0, 14, 14) };
        panel.Children.Add(Fluent.Divider());

        var live = entry.IsEnabled && !entry.IsLive;

        panel.Children.Add(new TextBlock
        {
            Text = live
                ? $"Not running — {entry.State}"
                : char.ToUpperInvariant(entry.State[0]) + entry.State[1..],
            Foreground = live ? Palette.Warning : Palette.Muted,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = entry.LastRun ?? "Not run since the agent started.",
            Foreground = Palette.Muted,
            FontSize = 12.5,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        if (entry.HealthNote is { Length: > 0 } note)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"You said: {note}",
                Foreground = Palette.Muted,
                FontSize = 12.5,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // The two halves of "does this actually do what I meant?". Ticking one clears the other;
        // unticking leaves it untested, which is the honest third state rather than a third box.
        var verdicts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0),
        };

        verdicts.Children.Add(Fluent.Check(
            "Works", entry.Health == HealthState.Works,
            on => SetVerdict(entry, on ? HealthState.Works : HealthState.Untested),
            Palette.Good));

        verdicts.Children.Add(Fluent.Check(
            "Not working", entry.Health == HealthState.NotWorking,
            on => SetVerdict(entry, on ? HealthState.NotWorking : HealthState.Untested),
            Palette.Danger));

        panel.Children.Add(verdicts);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0),
        };

        if (entry.NeedsApproval)
        {
            actions.Children.Add(Fluent.IconButton(
                Fluent.Read, "Review", () => Review(entry), Palette.Accent,
                "Read the plan and approve it"));
        }

        // Only once it has actually run. Offering repair for an automation with no transcript
        // produces a prompt whose most useful section says "there is no transcript".
        if (entry.LastRun is not null)
        {
            actions.Children.Add(Fluent.IconButton(
                Fluent.Repair, "Repair", () => Repair(entry), null,
                "Build a prompt to get this fixed"));
        }

        actions.Children.Add(Fluent.IconButton(
            Fluent.History, "History", () => History(entry), null,
            "Past versions, with a diff against what is on disk"));

        panel.Children.Add(actions);
        return panel;
    }

    /// <summary>
    /// Record a verdict, and go straight on to repair when it is a bad one.
    /// </summary>
    /// <remarks>
    /// The moment a user decides something is broken is the moment they know what is wrong with
    /// it. Asking later gets a vaguer answer, and a vague complaint makes a worse repair prompt.
    /// </remarks>
    private void SetVerdict(DashboardEntry entry, HealthState state)
    {
        if (refreshing)
        {
            return;
        }

        host.SetHealth(entry.FileName, state, state == entry.Health ? entry.HealthNote : null);
        Refresh();

        if (state == HealthState.NotWorking)
        {
            Repair(entry with { Health = state });
            return;
        }

        Say(state switch
        {
            HealthState.Works => $"{entry.Name} marked as working.",
            _ => $"{entry.Name} is untested again.",
        });
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
        var window = Fluent.Dialog(this, $"Review {entry.Name}", 760, 640);

        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        top.Children.Add(Fluent.Heading(entry.Name));
        top.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
            Text = $"{entry.Chord} will run this once approved. Read it first — approval is what "
                 + "stops a file dropped into the folder from running on a keypress.",
        });

        Grid.SetRow(top, 0);
        layout.Children.Add(top);

        var body = Fluent.CodePanel(entry.Preview);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var buttons = Fluent.Buttons(
            Fluent.IconButton(Fluent.Cross, "Close", window.Close),
            Fluent.Primary("I have read this — approve", Fluent.Tick, () =>
            {
                host.Approve(entry.FileName);
                window.Close();
                Refresh();
                Say($"{entry.Name} approved.");
            }));

        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);

        window.Content = layout;
        window.ShowDialog();
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

        var window = Fluent.Dialog(this, $"History of {entry.Name}", 560, 500);
        var list = new StackPanel();

        foreach (var version in history)
        {
            var caption = new StackPanel { Orientation = Orientation.Horizontal };
            caption.Children.Add(Fluent.Glyph(
                version.IsCurrent ? Fluent.Tick : Fluent.History, 13,
                version.IsCurrent ? Palette.Good : Palette.Muted));

            caption.Children.Add(new TextBlock
            {
                Text = version.IsCurrent ? $"{version.Summary}   ·   on disk now" : version.Summary,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5,
            });

            var row = new Button
            {
                Content = caption,
                Foreground = version.IsCurrent ? Palette.Muted : Palette.Text,
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !version.IsCurrent,
                Template = Fluent.ListButtonTemplate(),
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

        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Fluent.Heading("Version history"));
        top.Children.Add(new TextBlock
        {
            Text = "Pick a version to see what restoring it would change.",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
        });

        Grid.SetRow(top, 0);
        layout.Children.Add(top);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list,
        };

        scroller.Resources.Add(typeof(ScrollBar), Fluent.SlimScrollBar());
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

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

        var window = Fluent.Dialog(this, $"Repair {entry.Name}", 780, 660);

        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Fluent.Heading("What went wrong?"));
        top.Children.Add(new TextBlock
        {
            Text = "Describe what it did and what it should have done. That sentence, the plan "
                 + "and the run below all go into the prompt.",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
        });

        Grid.SetRow(top, 0);
        layout.Children.Add(top);

        var complaint = Fluent.Input(new TextBox
        {
            Text = entry.HealthNote ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 3,
            MaxLines = 6,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Grid.SetRow(complaint, 1);
        layout.Children.Add(complaint);

        var body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        body.Children.Add(new TextBlock
        {
            Text = run is null ? "It has not run yet." : "The last run",
            Foreground = Palette.Muted,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (run is not null)
        {
            var transcript = Fluent.CodePanel(run.Transcript);
            transcript.MaxHeight = 280;
            body.Children.Add(transcript);
        }

        Grid.SetRow(body, 2);
        layout.Children.Add(body);

        var said = new TextBlock
        {
            Foreground = Palette.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var buttons = Fluent.Buttons(
            Fluent.IconButton(Fluent.Cross, "Close", window.Close),
            Fluent.Primary("Copy repair prompt", Fluent.Repair, () =>
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
                    + "bring the corrected JSON back to New hotkey below.";
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

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        top.Children.Add(Fluent.IconButton(
            Fluent.Read, "Copy prompt for Claude Code", CopyPrompt, Palette.Accent,
            "Copy a prompt describing this, with the schema and the rules"));
        panel.Children.Add(top);

        panel.Children.Add(Label("Paste the JSON it gives you"));
        panel.Children.Add(pasted);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(Fluent.IconButton(Fluent.Tick, "Check", CheckPasted, null,
            "Validate it without saving"));
        bottom.Children.Add(Fluent.IconButton(Fluent.Read, "Preview", PreviewPasted, null,
            "Render it the way Review does"));
        bottom.Children.Add(Fluent.Primary("Save", Fluent.Add, SavePasted));
        panel.Children.Add(bottom);

        // The status line used to live here. It now sits above this panel, where it is visible
        // whether or not this is expanded.
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

    private static TextBox Field(int minLines) => Fluent.Input(new TextBox
    {
        AcceptsReturn = minLines > 1,
        TextWrapping = minLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MinLines = minLines,
        MaxLines = minLines > 1 ? minLines * 2 : 1,
        VerticalScrollBarVisibility = minLines > 1 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
    });
}

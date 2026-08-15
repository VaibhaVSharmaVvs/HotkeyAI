using System.IO;

namespace HotkeyAI.Agent;

/// <summary>
/// Notices when the automations folder changes, and says so once.
/// </summary>
/// <remarks>
/// The authoring loop is "write a plan, drop it in the folder", and until now the agent only
/// looked when asked. That made the last step of every authoring session a trip to the tray menu,
/// which is exactly the friction the risk register names as the reason people stop writing
/// automations.
/// <para>
/// <b>This cannot make anything run.</b> A file appearing here is classified by the store like
/// any other, which means new or edited content is inert until a person has read the rendered plan
/// and approved it. The watcher shortens the distance between saving a file and being *asked*; it
/// has no way to shorten the distance between a file existing and it being trusted, and it must
/// not acquire one.
/// </para>
/// </remarks>
internal sealed class AutomationWatcher : IDisposable
{
    /// <summary>
    /// How long the folder must be quiet before the change is acted on.
    /// </summary>
    /// <remarks>
    /// A single save is several events — editors write, rename and touch attributes, and a plan
    /// arriving over a sync client can land in pieces. Reacting to the first one means reading a
    /// half-written file and reporting it as invalid, so every event restarts this timer and only
    /// the settled state is ever read.
    /// </remarks>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(750);

    private readonly FileSystemWatcher watcher;
    private readonly Timer settle;
    private readonly Action onSettled;
    private bool disposed;

    public AutomationWatcher(string directory, Action onSettled)
    {
        this.onSettled = onSettled;

        Directory.CreateDirectory(directory);

        settle = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        watcher = new FileSystemWatcher(directory, "*.json")
        {
            // Size and LastWrite between them cover a save; FileName covers arriving, leaving and
            // being renamed, which is how most editors write a file.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };

        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnChanged;

        // Losing the handle is not fatal, but it is silent — the folder simply stops being
        // watched — so it is worth a line in the log rather than a mystery later.
        watcher.Error += (_, e) =>
            AgentLog.Line($"Watching {directory} failed: {e.GetException().Message}");

        watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        settle.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!disposed)
        {
            settle.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            onSettled();
        }
#pragma warning disable CA1031 // A watcher callback must never take the agent down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AgentLog.Line($"Reloading after a folder change failed: {ex}");
        }
    }
}

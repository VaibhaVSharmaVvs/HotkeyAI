using System.Windows.Threading;

namespace HotkeyAI.Ui;

/// <summary>
/// A dedicated STA thread with a WPF dispatcher, shared by every overlay.
/// </summary>
/// <remarks>
/// The agent's main thread already owns the hotkey message pump and must never be blocked — a
/// picker that is waiting for a choice would otherwise stop the panic key from being delivered.
/// So the overlays live on their own thread, started on first use and never torn down.
/// <para>
/// The thread is a background thread deliberately: the agent should exit when its main loop ends
/// without anyone having to remember to shut the UI down first.
/// </para>
/// </remarks>
public sealed class UiThread
{
    private static readonly Lazy<UiThread> Instance = new(() => new UiThread(), isThreadSafe: true);

    private readonly Dispatcher dispatcher;

    private UiThread()
    {
        using var ready = new ManualResetEventSlim(false);
        Dispatcher? started = null;

        var thread = new Thread(() =>
        {
            started = Dispatcher.CurrentDispatcher;

            // An exception on this thread would otherwise take the process down, and with it
            // every hotkey — because a toast failed to draw. Logged and swallowed: a broken
            // overlay is a bad afternoon, a dead agent is a broken product. The report is what
            // stops "swallowed" meaning "hidden".
            started.UnhandledException += (_, e) =>
            {
                Report?.Invoke($"UI thread: {e.Exception}");
                e.Handled = true;
            };

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "HotkeyAI overlay UI",
        };

        // WPF requires single-threaded apartment; without this the window never appears and the
        // failure is an obscure COM error rather than anything that names the cause.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        ready.Wait();
        dispatcher = started!;
    }

    /// <summary>The shared UI thread, started on first access.</summary>
    public static UiThread Shared => Instance.Value;

    /// <summary>Where UI-thread failures are reported. Set before the thread is first used.</summary>
    public static Action<string>? Report { get; set; }

    /// <summary>Run a function on the UI thread and await its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return dispatcher.InvokeAsync(work).Task;
    }

    /// <summary>Queue work on the UI thread without waiting for it.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        dispatcher.BeginInvoke(work);
    }
}

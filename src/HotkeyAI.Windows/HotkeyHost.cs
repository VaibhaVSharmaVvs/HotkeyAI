using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Windows;

/// <summary>Why a hotkey registration did or did not succeed.</summary>
/// <param name="Registered">Whether the chord is now ours.</param>
/// <param name="AlreadyTaken">
/// The chord is held by something else, or reserved by the shell. The API cannot tell those
/// apart and never names the holder — the Phase 0 spike confirmed both cases return the same
/// undifferentiated error.
/// </param>
/// <param name="ErrorCode">Raw Win32 error, for logs.</param>
public readonly record struct RegistrationResult(bool Registered, bool AlreadyTaken, int ErrorCode)
{
    /// <summary>What to tell the user. Deliberately does not speculate about the holder.</summary>
    public string Describe() => Registered
        ? "registered"
        : AlreadyTaken
            ? "unavailable — another application or Windows itself already holds this combination"
            : $"could not be registered (Win32 error {ErrorCode})";
}

/// <summary>
/// Owns the global hotkeys and the thread that receives them.
/// </summary>
/// <remarks>
/// <para>
/// <c>RegisterHotKey</c> binds a chord to the calling <i>thread</i> and posts <c>WM_HOTKEY</c> to
/// that thread's message queue, so registration and the message loop must live on the same
/// thread — this one. Callers hand work to it rather than calling Win32 themselves.
/// </para>
/// <para>
/// It deliberately does not retry. Registration is first-come-first-served, so retrying is a
/// contention war with another process that the user cannot see the score of; a failure is
/// reported once and left for a person to resolve.
/// </para>
/// </remarks>
public sealed class HotkeyHost : IDisposable
{
    private readonly Thread pump;
    private readonly ConcurrentQueue<Action> work = new();
    private readonly Dictionary<int, string> registered = [];
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private uint threadId;
    private int nextId = 1;
    private bool disposed;

    /// <summary>Raised on the pump thread when a registered chord is pressed.</summary>
    public event Action<string>? Pressed;

    public HotkeyHost()
    {
        pump = new Thread(Run)
        {
            IsBackground = true,
            Name = "HotkeyAI hotkey pump",
        };

        pump.SetApartmentState(ApartmentState.STA);
        pump.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Register a chord, associating it with a key the caller chooses.</summary>
    /// <param name="name">Identifies the automation when the chord fires.</param>
    /// <param name="chord">Modifiers plus one non-modifier key.</param>
    public RegistrationResult Register(string name, IReadOnlyList<KeyName> chord)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!KeyCodes.TrySplit(chord, out var modifiers, out var key))
        {
            return new RegistrationResult(false, false, 0);
        }

        return Invoke(() =>
        {
            var id = nextId++;

            // MOD_NOREPEAT: holding the chord must fire once, not once per auto-repeat.
            var ok = Native.RegisterHotKey(0, id, modifiers | KeyCodes.ModNoRepeat, key);

            if (ok)
            {
                registered[id] = name;
                return new RegistrationResult(true, false, 0);
            }

            var error = Marshal.GetLastWin32Error();
            return new RegistrationResult(
                false, error == Native.ERROR_HOTKEY_ALREADY_REGISTERED, error);
        });
    }

    /// <summary>
    /// Ask whether a chord could be registered right now, without keeping it.
    /// </summary>
    /// <remarks>
    /// Registers and immediately unregisters on the pump thread, because that is the only way to
    /// find out: there is no query API, and <c>RegisterHotKey</c> binds to the calling thread, so
    /// probing from anywhere else would answer a different question.
    /// <para>
    /// Two things this cannot tell you, both from the Phase 0 spike. It cannot name whoever holds
    /// a taken chord — every failure is the same undifferentiated 1409, whether the holder is
    /// another application or the shell. And a combination grabbed by a low-level keyboard hook,
    /// which is how context-sensitive AutoHotkey bindings and push-to-talk work, reports as
    /// <i>available</i> here and then never fires. Availability is necessary, not sufficient.
    /// </para>
    /// <para>
    /// A chord this host already holds reports as taken, which is true but unhelpful on its own —
    /// the caller knows its own bindings and should check those first so it can name the
    /// automation involved.
    /// </para>
    /// </remarks>
    public RegistrationResult Probe(IReadOnlyList<KeyName> chord)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!KeyCodes.TrySplit(chord, out var modifiers, out var key))
        {
            return new RegistrationResult(false, false, 0);
        }

        return Invoke(() =>
        {
            // An id that cannot collide with a live registration, since nextId only ever grows.
            const int ProbeId = int.MaxValue;

            if (!Native.RegisterHotKey(0, ProbeId, modifiers | KeyCodes.ModNoRepeat, key))
            {
                var error = Marshal.GetLastWin32Error();
                return new RegistrationResult(
                    false, error == Native.ERROR_HOTKEY_ALREADY_REGISTERED, error);
            }

            Native.UnregisterHotKey(0, ProbeId);
            return new RegistrationResult(true, false, 0);
        });
    }

    /// <summary>Release every chord this host holds.</summary>
    public void UnregisterAll() => Invoke(() =>
    {
        foreach (var id in registered.Keys)
        {
            Native.UnregisterHotKey(0, id);
        }

        registered.Clear();
        return true;
    });

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            UnregisterAll();
            Native.PostThreadMessage(threadId, Native.WM_QUIT, 0, 0);
            pump.Join(TimeSpan.FromSeconds(2));
        }
#pragma warning disable CA1031 // Shutdown must not throw.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // ---------------------------------------------------------------------------------

    /// <summary>Run a delegate on the pump thread and wait for its result.</summary>
    private T Invoke<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        work.Enqueue(() =>
        {
            try
            {
                completion.SetResult(action());
            }
#pragma warning disable CA1031 // Marshal the failure to the caller rather than killing the pump.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                completion.SetException(ex);
            }
        });

        // Wake the loop: GetMessage blocks until something arrives, and queued work is not a
        // message. WM_NULL exists precisely to nudge a thread that is waiting.
        Native.PostThreadMessage(threadId, Native.WM_NULL, 0, 0);

        return completion.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        threadId = Native.GetCurrentThreadId();

        // Force the thread to create its message queue before anyone posts to it: a
        // PostThreadMessage to a thread with no queue is silently lost.
        Native.PostThreadMessage(threadId, Native.WM_NULL, 0, 0);

        ready.SetResult();

        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Drain();

            if (message.Message != Native.WM_HOTKEY)
            {
                continue;
            }

            if (registered.TryGetValue((int)message.WParam, out var name))
            {
                Pressed?.Invoke(name);
            }
        }

        Drain();
    }

    private void Drain()
    {
        while (work.TryDequeue(out var item))
        {
            item();
        }
    }
}

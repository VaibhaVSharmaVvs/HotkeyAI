using System.Collections.Concurrent;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Ui;

namespace HotkeyAI.Agent;

/// <summary>What happened when a run was asked for.</summary>
/// <param name="Record">The run, or null if it never started.</param>
/// <param name="Refusal">Why it did not start, or null if it did.</param>
public sealed record RunAttempt(RunRecord? Record, string? Refusal);

/// <summary>
/// The one place an automation is executed.
/// </summary>
/// <remarks>
/// Both a hotkey press and a test run arrive here, and that is the point. The single-run gate,
/// the panic key and the transcript a repair prompt is built from all have to be the same ones
/// either way — a test run that bypassed the gate could race a hotkey press for the foreground
/// window, and the resulting log would describe a sequence neither plan contains.
/// <para>
/// It also means a test run is a real run. Nothing is stubbed or dry: the whole value of watching
/// one comes from it being the same execution the hotkey performs.
/// </para>
/// </remarks>
internal sealed class AutomationRunner(
    PlanExecutor executor,
    ConcurrentDictionary<string, RunRecord> lastRuns)
{
    private int running;

    /// <summary>
    /// The token source for the run in flight, or null when nothing is running.
    /// </summary>
    /// <remarks>
    /// Per run, and this is load-bearing. A single long-lived panic source cancels permanently:
    /// once pressed, its token stays cancelled, so every later automation aborts on its first
    /// action while the log cheerfully reports that hotkeys are live again. The panic key has to
    /// stop the automation that is running, not every automation that ever will.
    /// </remarks>
    private CancellationTokenSource? inFlight;

    /// <summary>True while an automation is executing.</summary>
    public bool IsBusy => Volatile.Read(ref running) != 0;

    /// <summary>
    /// Safety control 1: stop whatever is running, right now.
    /// </summary>
    /// <remarks>
    /// Deliberately does nothing when nothing is running, rather than arming a cancellation for
    /// the next run. Pressing the panic key out of habit must never be a way to break the next
    /// automation someone triggers.
    /// </remarks>
    public void Panic()
    {
        var source = Volatile.Read(ref inFlight);

        if (source is null)
        {
            AgentLog.Line("[panic] nothing is running.");
            return;
        }

        AgentLog.Line("[panic] stopping the running automation.");

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the read and the cancel. Nothing left to stop.
        }
    }

    /// <summary>
    /// Run a plan, unless one is already running.
    /// </summary>
    /// <param name="name">The automation's file name, which the log and the store key on.</param>
    /// <param name="plan">The validated plan.</param>
    /// <param name="how">How it was started, for the log line.</param>
    /// <param name="observer">A live watcher, for test-run mode. Null for a hotkey press.</param>
    /// <param name="stop">
    /// An extra cancellation, such as a Stop button. The panic key applies regardless, so no
    /// caller can produce a run that cannot be stopped from the keyboard.
    /// </param>
    public async Task<RunAttempt> RunAsync(
        string name,
        Automation plan,
        string how,
        IRunObserver? observer = null,
        CancellationToken stop = default)
    {
        // One at a time. Two automations racing for the foreground window would produce results
        // neither plan describes, and the logs would interleave into nonsense.
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            AgentLog.Line($"[{name}] ignored — another automation is still running.");
            return new RunAttempt(null, "Another automation is still running.");
        }

        try
        {
            return new RunAttempt(
                await ExecuteAsync(name, plan, how, observer, stop).ConfigureAwait(false),
                null);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private async Task<RunRecord?> ExecuteAsync(
        string name,
        Automation plan,
        string how,
        IRunObserver? observer,
        CancellationToken stop)
    {
        AgentLog.Line();
        AgentLog.Line($"[{name}] {how}");

        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);

        // Published before the first action and cleared in the finally, so the window in which
        // the panic key finds nothing to cancel is exactly the window in which nothing is running.
        Volatile.Write(ref inFlight, run);

        try
        {
            // Which of the two stopped it is knowable here and nowhere else: `stop` is the caller's
            // (a dashboard or test-run Stop button), and `run` cancelled without it is the panic key.
            // Security review 2026-08-17, finding L6 — the transcript used to blame the panic key
            // either way, and the transcript is what gets pasted into a repair prompt.
            var result = await executor
                .RunAsync(
                    plan,
                    observer,
                    () => stop.IsCancellationRequested
                        ? "Stopped by the Stop button."
                        : "Stopped by the panic key.",
                    run.Token)
                .ConfigureAwait(false);
            var transcript = result.ToTranscript();

            AgentLog.Raw(transcript);

            // Recorded for a test run exactly as for a hotkey press. A run the user watched is the
            // best evidence a repair prompt can have, and it would be perverse to keep the one
            // they were not looking at instead.
            var record = new RunRecord(
                DateTimeOffset.Now, result.Succeeded, result.UnverifiedCount, transcript);

            lastRuns[name] = record;
            return record;
        }
#pragma warning disable CA1031 // A failing automation must never take the agent down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AgentLog.Line($"[{name}] the engine failed unexpectedly: {ex.Message}");
            return null;
        }
        finally
        {
            Volatile.Write(ref inFlight, null);
        }
    }
}

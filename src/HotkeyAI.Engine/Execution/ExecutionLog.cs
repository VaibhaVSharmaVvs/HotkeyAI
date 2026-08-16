using System.Globalization;
using System.Text;

namespace HotkeyAI.Engine.Execution;

/// <summary>How a single action turned out.</summary>
public enum StepOutcome
{
    Succeeded,
    Failed,

    /// <summary>Not run — an <c>if</c> branch that was not taken, or the run had already stopped.</summary>
    Skipped,

    /// <summary>Stopped by the panic key, a cap, or an <c>abort</c> action.</summary>
    Aborted,
}

/// <summary>Whether the engine could confirm an action had its intended effect.</summary>
public enum Verification
{
    /// <summary>No postcondition. Reported to the user as unverified, never as success.</summary>
    None,

    Passed,

    Failed,
}

/// <summary>One line of the execution log.</summary>
/// <param name="At">When the action finished.</param>
/// <param name="ActionId">The plan's id for the action, when it declared one.</param>
/// <param name="ActionType">The DSL type, e.g. <c>launch_process</c>.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Verification">Whether the postcondition was checked, and its result.</param>
/// <param name="Detail">Human-readable summary, safe to show and to log.</param>
public sealed record LogEntry(
    DateTimeOffset At,
    string? ActionId,
    string ActionType,
    StepOutcome Outcome,
    Verification Verification,
    string Detail)
{
    public override string ToString()
    {
        var id = string.IsNullOrEmpty(ActionId) ? "" : $" [{ActionId}]";
        var check = Verification switch
        {
            Verification.Passed => " (verified)",
            Verification.Failed => " (verification FAILED)",
            _ => Outcome == StepOutcome.Succeeded ? " (unverified)" : "",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{At:HH:mm:ss}{id} {ActionType}: {Outcome}{check} — {Detail}");
    }
}

/// <summary>
/// Watches a run while it happens, rather than reading it afterwards.
/// </summary>
/// <remarks>
/// Exists for test-run mode. <see cref="ExecutionResult"/> arrives only once the plan has
/// finished, which is too late for the one question a person testing an automation actually has:
/// <em>which step is it stuck on right now?</em> A transcript that appears only after the run
/// answers that by omission — you see two lines, then nothing, and the plan may be hung, waiting,
/// or done.
/// <para>
/// <see cref="Starting"/> is what makes the difference, because an action is logged when it
/// <em>finishes</em>. Without it a ten-second wait shows nothing at all while it waits.
/// </para>
/// <para>
/// Implementations must not throw. The executor swallows anything that escapes — a watcher is an
/// onlooker, and a broken one must never be able to stop an automation that was working.
/// </para>
/// </remarks>
public interface IRunObserver
{
    /// <summary>An action is about to run.</summary>
    /// <param name="actionType">The DSL type, e.g. <c>launch_process</c>.</param>
    /// <param name="actionId">The plan's id for it, when it declared one.</param>
    void Starting(string actionType, string? actionId);

    /// <summary>An action finished. The entry is the same one the transcript will hold.</summary>
    void Finished(LogEntry entry);
}

/// <summary>The outcome of a whole run.</summary>
/// <param name="Succeeded">True only if every action completed and no postcondition failed.</param>
/// <param name="Entries">The full log, in order.</param>
/// <param name="FailureReason">Why the run stopped, when it did not complete.</param>
/// <param name="FailedActionId">Which action stopped it.</param>
public sealed record ExecutionResult(
    bool Succeeded,
    IReadOnlyList<LogEntry> Entries,
    string? FailureReason,
    string? FailedActionId)
{
    /// <summary>Actions that ran but could not be verified.</summary>
    /// <remarks>
    /// Surfaced separately because "it ran" and "it worked" are different claims, and the UI
    /// must not present the first as the second.
    /// </remarks>
    public int UnverifiedCount =>
        Entries.Count(e => e is
            { Outcome: StepOutcome.Succeeded, Verification: Verification.None });

    /// <summary>The log as text, for a repair prompt or a support paste.</summary>
    public string ToTranscript()
    {
        var text = new StringBuilder();

        foreach (var entry in Entries)
        {
            text.AppendLine(entry.ToString());
        }

        if (!Succeeded)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"FAILED: {FailureReason}");
        }
        else if (UnverifiedCount > 0)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Completed, but {UnverifiedCount} action(s) could not be verified.");
        }

        return text.ToString();
    }
}

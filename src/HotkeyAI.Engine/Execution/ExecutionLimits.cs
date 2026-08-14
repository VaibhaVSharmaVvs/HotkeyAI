namespace HotkeyAI.Engine.Execution;

/// <summary>
/// Hard stops applied to every run.
/// </summary>
/// <remarks>
/// Safety control 1. These are not tuning knobs — they are what stops a plan that steals focus
/// in a loop or hammers keystrokes from making the desktop unusable. The panic key is the
/// user's escape hatch; these are the engine's, for when the user cannot get to the keyboard.
/// </remarks>
public sealed record ExecutionLimits
{
    /// <summary>Most actions one run may execute, nested ones included.</summary>
    public int MaxSteps { get; init; } = 200;

    /// <summary>Wall-clock cap on a whole run.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Applied to an action that sets no <c>timeoutMs</c> of its own.</summary>
    public TimeSpan DefaultActionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for a postcondition when it names no <c>withinMs</c>.</summary>
    public TimeSpan DefaultVerificationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gap between postcondition polls.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    public static ExecutionLimits Default { get; } = new();
}

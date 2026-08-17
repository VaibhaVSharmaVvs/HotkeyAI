namespace HotkeyAI.Core.Dsl;

/// <summary>
/// What makes a key combination bindable.
/// </summary>
/// <remarks>
/// Extracted so the policy validator and the capture control in the dashboard apply one rule
/// rather than two that agree today. The UI has to answer "can I bind this?" the instant a key is
/// pressed, and a second implementation of the same rule would drift — the version that tells the
/// user something is fine while the validator refuses it is the worst of the two outcomes.
/// <para>
/// The rules themselves come from the Phase 0 spike. <c>RegisterHotKey</c> will happily bind a
/// bare key and swallow it system-wide, so requiring a modifier is load-bearing rather than
/// belt-and-braces: nothing in the OS prevents it, and the user would have no way to find out
/// which application ate their <c>P</c> key.
/// </para>
/// </remarks>
public static class HotkeyChord
{
    /// <summary>
    /// Everything wrong with a chord, phrased for a person. Empty means it can be bound.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<KeyName> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var problems = new List<string>();
        var nonModifiers = keys.Count(k => !Keys.IsModifier(k));

        if (nonModifiers != 1)
        {
            problems.Add(
                $"A hotkey needs exactly one non-modifier key, found {nonModifiers}. "
                + "Example: [\"CTRL\", \"ALT\", \"P\"].");
        }

        if (!keys.Any(Keys.IsModifier))
        {
            problems.Add(
                "A hotkey needs at least one modifier (CTRL, ALT, SHIFT or WIN). Registering a "
                + "bare key would swallow that key system-wide.");
        }

        if (keys.Distinct().Count() != keys.Count)
        {
            problems.Add("The chord repeats a key.");
        }

        if (IsPanic(keys))
        {
            problems.Add(
                "Ctrl+Alt+Shift+Esc is the panic key, which stops a running automation. "
                + "Binding an automation to it would take away the only way to abort one from "
                + "the keyboard.");
        }

        return problems;
    }

    /// <summary>
    /// The chord reserved for aborting a running automation.
    /// </summary>
    /// <remarks>
    /// Here rather than only in the agent, because <see cref="Problems"/> is the one rule the
    /// validator, the CLI and the dashboard all consult. Security review 2026-08-17, finding H4:
    /// the dashboard refused this chord when captured through the UI while the validator accepted
    /// it in hand-authored JSON — and hand-authored JSON is V1's primary authoring path, so the
    /// rule existed everywhere except the road people actually use. That is exactly the drift this
    /// type was extracted to prevent.
    /// </remarks>
    public static IReadOnlyList<KeyName> Panic { get; } =
        [KeyName.Ctrl, KeyName.Alt, KeyName.Shift, KeyName.Esc];

    /// <summary>Whether a chord is the panic key, however it was ordered.</summary>
    public static bool IsPanic(IReadOnlyList<KeyName> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return keys.Count == Panic.Count && Normalise(keys).SequenceEqual(Normalise(Panic));
    }

    /// <summary>Whether this chord is well-formed enough to try registering.</summary>
    /// <remarks>
    /// Says nothing about whether the combination is <i>free</i>. Only the OS can answer that,
    /// and even it cannot answer it completely — see the availability caveat in the spike notes.
    /// </remarks>
    public static bool IsBindable(IReadOnlyList<KeyName> keys) => Problems(keys).Count == 0;

    /// <summary>
    /// Put a chord into the order people write and read it: modifiers first, key last.
    /// </summary>
    /// <remarks>
    /// Capture reports whatever order the keyboard state happened to enumerate. Normalising here
    /// means two people pressing the same combination get the same JSON, and a re-bind to the
    /// chord an automation already has produces an identical file rather than a spurious change
    /// that would revoke its approval.
    /// </remarks>
    public static IReadOnlyList<KeyName> Normalise(IReadOnlyList<KeyName> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        // Ctrl, Alt, Shift, Win — the order they are written in every Windows dialog.
        var order = new[] { KeyName.Ctrl, KeyName.Alt, KeyName.Shift, KeyName.Win };

        return
        [
            .. order.Where(keys.Contains),
            .. keys.Where(k => !Keys.IsModifier(k)),
        ];
    }
}

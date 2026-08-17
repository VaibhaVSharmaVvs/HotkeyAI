namespace HotkeyAI.Core.Matching;

/// <summary>
/// Deciding which hotkeys survive what someone typed into the search box.
/// </summary>
/// <remarks>
/// In Core rather than in the WPF project, for the same reason <see cref="FuzzyMatcher"/> is:
/// a filter that quietly drops the row you were looking for looks identical to a list that
/// simply does not contain it. That is invisible in a screenshot and costs you the automation
/// you were trying to find, so it belongs somewhere with tests that run on Linux CI.
/// <para>
/// Two ways to match, because people look for a hotkey by two different handles. The name goes
/// through the fuzzy matcher, so <c>cd</c> finds "Close Distractions" exactly as it would in
/// the picker. The chord is matched on its letters and digits alone, so every way of writing a
/// combination — <c>ctrl+alt+n</c>, <c>ctrl alt n</c>, <c>CtrlAltN</c> — finds the same row.
/// Somebody hunting for the thing on Alt+N should not have to guess the separator.
/// </para>
/// <para>
/// It filters and never reorders. The picker ranks because it is choosing one item; this is a
/// settings list, where rows moving under the cursor as you type is a way to click the wrong
/// one.
/// </para>
/// </remarks>
public static class HotkeySearch
{
    /// <summary>Whether one hotkey should stay visible for this query.</summary>
    /// <param name="name">The automation's display name.</param>
    /// <param name="chord">Its rendered trigger, such as <c>Ctrl + Alt + N</c>.</param>
    /// <param name="query">What the user typed. Blank matches everything.</param>
    public static bool Matches(string name, string chord, string query)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var trimmed = query.Trim();

        if (FuzzyMatcher.Match(name, trimmed) is not null)
        {
            return true;
        }

        // Separators are noise here. "ctrl+alt+n", "ctrl alt n" and "CtrlAltN" are the same
        // request, and the rendered chord uses spaced plus signs that nobody types.
        var wanted = Letters(trimmed);

        return wanted.Length > 0
               && Letters(chord).Contains(wanted, StringComparison.Ordinal);
    }

    /// <summary>Lower-case letters and digits only.</summary>
    private static string Letters(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                kept.Append(char.ToLowerInvariant(character));
            }
        }

        return kept.ToString();
    }
}

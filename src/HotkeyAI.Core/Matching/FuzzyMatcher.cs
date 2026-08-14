namespace HotkeyAI.Core.Matching;

/// <summary>What a query matched in one candidate string.</summary>
/// <param name="Score">Higher is better. Only comparable between results for the same query.</param>
/// <param name="Positions">
/// Indices in the candidate that the query matched, ascending. The picker renders these in bold,
/// which is the whole reason the matcher reports them rather than just a score.
/// </param>
public readonly record struct FuzzyResult(int Score, IReadOnlyList<int> Positions);

/// <summary>
/// Subsequence matching with positional scoring — the ranking behind the picker overlay.
/// </summary>
/// <remarks>
/// This lives in Core, and is deliberately not in the WPF project, because it is the only part of
/// the picker that can be meaningfully unit tested. Ranking is where a picker is actually good or
/// bad: typing "hk" should offer <c>HotkeyAI</c> before <c>the-quick-brown</c>, and no amount of
/// clicking through a UI proves that reliably. Keeping it as a pure function in a plain
/// <c>net10.0</c> assembly means the behaviour is pinned by tests that run on Linux CI, and the
/// Windows layer above it stays a renderer with no judgement of its own.
/// </remarks>
public static class FuzzyMatcher
{
    /// <summary>Awarded for each matched character, before bonuses.</summary>
    private const int MatchScore = 16;

    /// <summary>
    /// Awarded when a match directly follows the previous one.
    /// </summary>
    /// <remarks>
    /// Larger than <see cref="WordStartBonus"/> on purpose. Typing "os" should offer
    /// <c>os-notes</c> — where the query is a literal prefix — above <c>open-solution</c>, where
    /// the same letters are two word initials. Both are defensible matches; the run of adjacent
    /// characters is the one people mean, and it is the one they will press Enter on.
    /// </remarks>
    private const int ConsecutiveBonus = 25;

    /// <summary>Awarded at the start of a word — after a separator, or at index 0.</summary>
    private const int WordStartBonus = 30;

    /// <summary>Awarded at an interior capital, so "oS" finds <c>openSolution</c>.</summary>
    private const int CamelBonus = 20;

    /// <summary>Awarded when the case matches exactly, to break ties toward the obvious answer.</summary>
    private const int ExactCaseBonus = 4;

    /// <summary>Charged per character skipped <i>between</i> two matched characters.</summary>
    private const int GapPenalty = 3;

    /// <summary>
    /// Charged per character skipped before the first match, and capped.
    /// </summary>
    /// <remarks>
    /// Far gentler than <see cref="GapPenalty"/>, and capped, because the items being ranked are
    /// usually absolute paths. Charging the full gap penalty for the run-up would mean
    /// <c>C:\Users\me\src\hotkey-ai</c> scored badly for "hotkey" purely because of how deep it
    /// sits, and every deep path would lose to a shallow one regardless of how well it matched.
    /// A small, bounded charge still prefers the earlier of two otherwise equal matches.
    /// </remarks>
    private const int LeadingPenaltyPerChar = 1;

    /// <summary>Ceiling on the run-up charge. See <see cref="LeadingPenaltyPerChar"/>.</summary>
    private const int MaxLeadingPenalty = 15;

    /// <summary>
    /// Longest candidate the matcher will score in full.
    /// </summary>
    /// <remarks>
    /// Cost is proportional to query length times candidate length, and this runs on every
    /// keystroke over every item. Paths are the realistic worst case and are nowhere near this;
    /// the cap exists so that one pathological item cannot make the overlay feel slow.
    /// </remarks>
    private const int MaxCandidateLength = 512;

    private static readonly char[] Separators = [' ', '\\', '/', '-', '_', '.', ':'];

    /// <summary>
    /// Score one candidate against a query, or return null when the query is not a subsequence.
    /// </summary>
    /// <remarks>
    /// An empty query matches everything with a score of zero, which is what keeps the picker
    /// showing the full list before the user has typed anything.
    /// </remarks>
    public static FuzzyResult? Match(string candidate, string query)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Length == 0)
        {
            return new FuzzyResult(0, []);
        }

        var n = Math.Min(candidate.Length, MaxCandidateLength);
        var m = query.Length;

        if (m > n)
        {
            return null;
        }

        // dp[j, i] is the best score for a match of query[0..j] in which query[j] lands on
        // candidate[i]. Unreachable combinations stay at Unreachable and are never chosen.
        const int Unreachable = int.MinValue / 2;

        var dp = new int[m, n];
        var from = new int[m, n];

        for (var j = 0; j < m; j++)
        {
            // best is the highest score reachable from the previous query character at any
            // position strictly before i, already decayed by the gap penalty for everything
            // skipped since. Carrying it forward is what keeps this linear in candidate length
            // instead of quadratic. It is only meaningful for j > 0; the first query character
            // has no predecessor and is charged the run-up penalty instead.
            var best = Unreachable;
            var bestIndex = -1;

            for (var i = 0; i < n; i++)
            {
                int reached;
                int parent;

                if (j == 0)
                {
                    reached = -Math.Min(i * LeadingPenaltyPerChar, MaxLeadingPenalty);
                    parent = -1;
                }
                else
                {
                    reached = best;
                    parent = bestIndex;

                    // Landing directly after the previous match is the case worth paying for,
                    // and it is not the same as the running best — it has no gap to decay.
                    if (i > 0 && dp[j - 1, i - 1] > Unreachable
                        && dp[j - 1, i - 1] + ConsecutiveBonus > reached)
                    {
                        reached = dp[j - 1, i - 1] + ConsecutiveBonus;
                        parent = i - 1;
                    }
                }

                if (reached > Unreachable && SameLetter(candidate[i], query[j]))
                {
                    var score = reached + MatchScore + Bonus(candidate, i);

                    if (candidate[i] == query[j])
                    {
                        score += ExactCaseBonus;
                    }

                    dp[j, i] = score;
                    from[j, i] = parent;
                }
                else
                {
                    dp[j, i] = Unreachable;
                }

                // Advance the running best from "before i" to "before i + 1".
                if (j > 0)
                {
                    var decayed = best <= Unreachable ? Unreachable : best - GapPenalty;

                    if (dp[j - 1, i] > decayed)
                    {
                        best = dp[j - 1, i];
                        bestIndex = i;
                    }
                    else
                    {
                        best = decayed;
                    }
                }
            }
        }

        var total = Unreachable;
        var endIndex = -1;

        for (var i = 0; i < n; i++)
        {
            if (dp[m - 1, i] > total)
            {
                total = dp[m - 1, i];
                endIndex = i;
            }
        }

        if (endIndex < 0 || total <= Unreachable)
        {
            return null;
        }

        var positions = new int[m];

        for (var j = m - 1; j >= 0; j--)
        {
            positions[j] = endIndex;
            endIndex = from[j, endIndex];
        }

        return new FuzzyResult(total, positions);
    }

    /// <summary>
    /// Filter and rank a list, keeping the caller's order as the tie-break.
    /// </summary>
    /// <remarks>
    /// Ties are broken by the shorter candidate and then by original position, never by text.
    /// Stability matters more than cleverness here: the list is being read by someone about to
    /// press Enter, and items that reshuffle between keystrokes cause the wrong selection.
    /// </remarks>
    public static IReadOnlyList<(int Index, FuzzyResult Result)> Rank(
        IReadOnlyList<string> candidates, string query)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(query);

        // With nothing typed there is nothing to rank by, and every score is zero. Sorting on the
        // tie-breaks would reorder the list for no reason — the user would open the picker and
        // find their items shuffled before touching the keyboard.
        if (query.Length == 0)
        {
            return [.. Enumerable.Range(0, candidates.Count)
                .Select(i => (i, new FuzzyResult(0, Array.Empty<int>())))];
        }

        var matches = new List<(int Index, FuzzyResult Result)>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            if (Match(candidates[i], query) is { } result)
            {
                matches.Add((i, result));
            }
        }

        return [.. matches
            .OrderByDescending(m => m.Result.Score)
            .ThenBy(m => candidates[m.Index].Length)
            .ThenBy(m => m.Index)];
    }

    private static bool SameLetter(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    private static int Bonus(string candidate, int index)
    {
        if (index == 0)
        {
            return WordStartBonus;
        }

        var previous = candidate[index - 1];

        if (Array.IndexOf(Separators, previous) >= 0)
        {
            return WordStartBonus;
        }

        // An interior capital after a lowercase letter starts a word in every naming convention
        // the file system throws at this — OpenSolution, myProject, HotkeyAI.
        return char.IsUpper(candidate[index]) && char.IsLower(previous) ? CamelBonus : 0;
    }
}

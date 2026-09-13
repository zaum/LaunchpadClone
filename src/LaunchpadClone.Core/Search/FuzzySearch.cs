namespace LaunchpadClone.Core.Search;

/// <summary>
/// Lightweight fuzzy matcher for app names, in the spirit of editor
/// "go to file" search. Zero UI dependencies so it stays unit-testable and
/// reusable from any frontend.
///
/// Scoring model (higher = better, 0 = no match):
/// - A plain substring hit beats everything; earlier and exact-length hits
///   score higher.
/// - Otherwise every query character must appear in order; consecutive
///   runs, word-boundary hits and tight clustering earn bonuses, long gaps
///   are penalized. Short names get a mild boost so "bit" ranks "Bits &
///   Bye" above a long program whose name merely contains the letters.
/// </summary>
public static class FuzzySearch
{
    /// <summary>
    /// Scores how well <paramref name="target"/> matches the fuzzy
    /// <paramref name="query"/>. Returns 0 when there is no match.
    /// </summary>
    public static double Score(string? query, string? target)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(target))
            return 0;

        return ScoreCore(q.ToLowerInvariant(), target.ToLowerInvariant());
    }

    private static double ScoreCore(string q, string t)
    {
        // 1) Substring — the strongest, most predictable signal.
        var index = t.IndexOf(q, StringComparison.Ordinal);
        if (index >= 0)
        {
            double score = 200 - index;              // earlier match ranks higher
            if (index == 0)
                score += 60;                         // name starts with the query
            else if (IsWordBoundary(t, index))
                score += 30;
            if (t.Length == q.Length)
                score += 80;                         // exact full match
            return score / Math.Log(t.Length + 2);   // mild short-name boost
        }

        // 2) In-order subsequence with streak/boundary/gap adjustments.
        double running = 0;
        double streak = 0;
        var gaps = 0;
        var boundaries = 0;
        var lastMatchIndex = -1;
        var queryIndex = 0;

        for (var targetIndex = 0;
             targetIndex < t.Length && queryIndex < q.Length;
             targetIndex++)
        {
            if (t[targetIndex] != q[queryIndex])
                continue;

            streak = targetIndex == lastMatchIndex + 1 ? streak + 1 : 0;
            running += 10 + streak * 8;              // consecutive runs are strong
            if (IsWordBoundary(t, targetIndex))
            {
                running += 14;
                boundaries++;
            }
            if (lastMatchIndex >= 0)
                gaps += targetIndex - lastMatchIndex - 1;

            lastMatchIndex = targetIndex;
            queryIndex++;
        }

        if (queryIndex < q.Length)
            return 0; // some query characters never matched in order

        var subsequenceScore = running - gaps * 0.8 + boundaries * 2;
        return Math.Max(1, subsequenceScore) / Math.Log(t.Length + 2);
    }

    // A match right after a separator or at the start of the string reads as
    // "start of a word" — the way people actually type app names.
    private static bool IsWordBoundary(string s, int index) =>
        index == 0
        || char.IsWhiteSpace(s[index - 1])
        || s[index - 1] is '-' or '_' or '.' or '(' or '/' or '@';
}

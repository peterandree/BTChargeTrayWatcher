namespace BTChargeTrayWatcher.Utilities;

/// <summary>
/// Jaro-Winkler string similarity. Returns a value in [0.0, 1.0].
/// 1.0 = identical strings. Used by ADR-015 Stage 4 fuzzy alias matching.
/// </summary>
internal static class JaroWinkler
{
    private const double PrefixScaleP = 0.1;

    /// <summary>
    /// Computes Jaro-Winkler similarity between <paramref name="s1"/> and <paramref name="s2"/>.
    /// Both strings are compared as-is; normalise before calling if case-insensitive comparison
    /// is required.
    /// </summary>
    internal static double Similarity(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;

        int matchWindow = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchWindow < 0) matchWindow = 0;

        var s1Matched = new bool[s1.Length];
        var s2Matched = new bool[s2.Length];

        int matches = 0;
        int transpositions = 0;

        for (int i = 0; i < s1.Length; i++)
        {
            int start = Math.Max(0, i - matchWindow);
            int end = Math.Min(i + matchWindow + 1, s2.Length);

            for (int j = start; j < end; j++)
            {
                if (s2Matched[j] || s1[i] != s2[j]) continue;
                s1Matched[i] = true;
                s2Matched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matched[i]) continue;
            while (!s2Matched[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        double jaro = (matches / (double)s1.Length
                    + matches / (double)s2.Length
                    + (matches - transpositions / 2.0) / matches) / 3.0;

        // Winkler prefix bonus
        int prefix = 0;
        for (int i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }

        return jaro + prefix * PrefixScaleP * (1.0 - jaro);
    }
}

using System.Text.RegularExpressions;

namespace BTChargeTrayWatcher.Utilities;

/// <summary>
/// Normalises a Bluetooth device display name for fuzzy comparison (ADR-015 Stage 3).
/// Strips punctuation, collapses whitespace, lowercases. Digits are preserved.
/// </summary>
internal static partial class DeviceNameNormalizer
{
    [GeneratedRegex(@"[^\w\d\s]", RegexOptions.Compiled)]
    private static partial Regex NonAlphanumericPunctuationRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>
    /// Returns a normalised version of <paramref name="name"/>:
    /// trimmed, lowercased, non-alphanumeric punctuation removed,
    /// runs of whitespace collapsed to a single space.
    /// </summary>
    internal static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var stripped = NonAlphanumericPunctuationRegex().Replace(name, " ");
        var collapsed = WhitespaceRunRegex().Replace(stripped, " ");
        return collapsed.Trim().ToLowerInvariant();
    }
}

using BTChargeTrayWatcher.Utilities;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Unit tests for ADR-015 alias resolution pipeline in <see cref="BatteryReaderOrchestrator"/>.
/// The orchestrator is not directly instantiable in tests (requires real GATT/Classic infra),
/// so alias logic is exercised via <see cref="ThresholdSettings"/> + white-box helpers
/// for Stages 1-4, plus integration through the public surface of
/// <see cref="DeviceNameNormalizer"/> and <see cref="JaroWinkler"/>.
/// </summary>
public sealed class AliasResolutionTests
{
    // ── DeviceNameNormalizer ─────────────────────────────────────────────────────────

    [Fact]
    public void Normalizer_strips_punctuation_and_lowercases()
    {
        var result = DeviceNameNormalizer.Normalize("Sony WH-1000XM5");
        Assert.Equal("sony wh 1000xm5", result);
    }

    [Fact]
    public void Normalizer_collapses_whitespace()
    {
        var result = DeviceNameNormalizer.Normalize("  My   Headset  ");
        Assert.Equal("my headset", result);
    }

    [Fact]
    public void Normalizer_returns_empty_for_whitespace_only_input()
    {
        Assert.Equal(string.Empty, DeviceNameNormalizer.Normalize("   "));
    }

    // ── JaroWinkler ─────────────────────────────────────────────────────────────────

    [Fact]
    public void JaroWinkler_identical_strings_return_1()
    {
        Assert.Equal(1.0, JaroWinkler.Similarity("abc", "abc"));
    }

    [Fact]
    public void JaroWinkler_completely_different_strings_return_below_threshold()
    {
        double score = JaroWinkler.Similarity("abc", "xyz");
        Assert.True(score < 0.92, $"Expected score < 0.92 but got {score}");
    }

    [Fact]
    public void JaroWinkler_similar_strings_score_above_threshold()
    {
        // "sony wh1000xm4" vs "sony wh1000xm5" — one character difference at the end
        double score = JaroWinkler.Similarity("sony wh1000xm4", "sony wh1000xm5");
        Assert.True(score >= 0.92, $"Expected score >= 0.92 but got {score}");
    }

    // ── ThresholdSettings AliasMap API ───────────────────────────────────────────────

    [Fact]
    public void AddAlias_and_retrieve_via_AliasMap()
    {
        var settings = new ThresholdSettings();
        settings.AddAlias("Sony Headphones", "bt-device-id-001");

        Assert.True(settings.AliasMap.ContainsKey("Sony Headphones"));
        Assert.Equal("bt-device-id-001", settings.AliasMap["Sony Headphones"]);
    }

    [Fact]
    public void AliasMap_lookup_is_case_insensitive()
    {
        var settings = new ThresholdSettings();
        settings.AddAlias("Sony Headphones", "bt-device-id-001");

        Assert.True(settings.AliasMap.ContainsKey("SONY HEADPHONES"));
    }

    [Fact]
    public void RemoveAlias_removes_entry_and_raises_Changed()
    {
        var settings = new ThresholdSettings();
        settings.AddAlias("Sony Headphones", "bt-device-id-001");

        int changed = 0;
        settings.Changed += () => changed++;
        settings.RemoveAlias("Sony Headphones");

        Assert.False(settings.AliasMap.ContainsKey("Sony Headphones"));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void RemoveAlias_noop_when_key_absent()
    {
        var settings = new ThresholdSettings();
        int changed = 0;
        settings.Changed += () => changed++;
        settings.RemoveAlias("NonExistent");  // must not throw, must not fire Changed
        Assert.Equal(0, changed);
    }

    [Fact]
    public void SetAliasMap_replaces_wholesale()
    {
        var settings = new ThresholdSettings();
        settings.AddAlias("Old Name", "id-old");
        settings.SetAliasMap(new Dictionary<string, string>
        {
            ["New Name"] = "id-new"
        });

        Assert.False(settings.AliasMap.ContainsKey("Old Name"));
        Assert.True(settings.AliasMap.ContainsKey("New Name"));
    }

    [Fact]
    public void AddAlias_rejects_empty_nameVariant()
    {
        var settings = new ThresholdSettings();
        Assert.Throws<ArgumentException>(() => settings.AddAlias("", "id"));
    }

    [Fact]
    public void AddAlias_rejects_empty_canonicalDeviceId()
    {
        var settings = new ThresholdSettings();
        Assert.Throws<ArgumentException>(() => settings.AddAlias("Name", ""));
    }

    // ── Stage 2: exact AliasMap name match ───────────────────────────────────────────

    [Fact]
    public void Stage2_exact_name_match_resolves_canonical_id()
    {
        // Verify via the normalizer + dictionary that Stage 2 would hit:
        // AliasMap["Sony WH-1000XM4"] = "canonical-id"
        // device.Name = "Sony WH-1000XM4" → exact OrdinalIgnoreCase match
        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sony WH-1000XM4"] = "canonical-id"
        };
        Assert.True(aliasMap.TryGetValue("sony wh-1000xm4", out var resolved));
        Assert.Equal("canonical-id", resolved);
    }

    // ── Stage 3: normalised name match ───────────────────────────────────────────────

    [Fact]
    public void Stage3_normalized_name_match_finds_alias()
    {
        // "Sony WH-1000XM4" normalised = "sony wh 1000xm4"
        // AliasMap key "Sony WH.1000XM4" normalised = "sony wh 1000xm4" → match
        var normalized1 = DeviceNameNormalizer.Normalize("Sony WH-1000XM4");
        var normalized2 = DeviceNameNormalizer.Normalize("Sony WH.1000XM4");
        Assert.Equal(normalized1, normalized2);
    }

    // ── Stage 4: fuzzy suggestion ────────────────────────────────────────────────────

    [Fact]
    public void Stage4_near_match_scores_above_threshold()
    {
        // Generation variant: "Sony WH-1000XM4" → "Sony WH-1000XM5"
        double score = JaroWinkler.Similarity(
            DeviceNameNormalizer.Normalize("Sony WH-1000XM4"),
            DeviceNameNormalizer.Normalize("Sony WH-1000XM5"));
        Assert.True(score >= 0.92, $"Score was {score}");
    }

    [Fact]
    public void Stage4_low_confidence_scores_below_threshold()
    {
        double score = JaroWinkler.Similarity(
            DeviceNameNormalizer.Normalize("Sony Headphones"),
            DeviceNameNormalizer.Normalize("Jabra Evolve2 85"));
        Assert.True(score < 0.92, $"Score was {score}");
    }
}

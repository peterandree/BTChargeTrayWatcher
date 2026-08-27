using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for DeviceProfileClassifier.Classify.
/// Uses documented Bluetooth major class codes from Assigned Numbers §2.8.2.
/// </summary>
public sealed class DeviceProfileClassifierTests
{
    private readonly DeviceProfileClassifier _classifier = new();

    // ── Transport classification ──────────────────────────────────────────────────────

    [Fact]
    public void Ble_only_classifies_as_Ble_transport()
    {
        var profile = _classifier.Classify(isBle: true, isClassic: false, classOfDevice: null);
        Assert.Equal(DeviceTransport.Ble, profile.Transport);
    }

    [Fact]
    public void Classic_only_classifies_as_Classic_transport()
    {
        var profile = _classifier.Classify(isBle: false, isClassic: true, classOfDevice: null);
        Assert.Equal(DeviceTransport.Classic, profile.Transport);
    }

    [Fact]
    public void Both_ble_and_classic_classifies_as_DualMode()
    {
        var profile = _classifier.Classify(isBle: true, isClassic: true, classOfDevice: null);
        Assert.Equal(DeviceTransport.DualMode, profile.Transport);
    }

    [Fact]
    public void Neither_ble_nor_classic_classifies_as_Unknown_transport()
    {
        var profile = _classifier.Classify(isBle: false, isClassic: false, classOfDevice: null);
        Assert.Equal(DeviceTransport.Unknown, profile.Transport);
    }

    // ── Category classification from CoD major class ──────────────────────────────────

    [Fact]
    public void Major_class_0x04_classifies_as_Audio()
    {
        // CoD: bits 12-8 = 0x04 (Audio/Video), rest can be anything
        uint cod = 0x0400 | 0x0020; // major=0x04, minor=0x10 (headphones)
        var profile = _classifier.Classify(isBle: true, isClassic: false, cod);
        Assert.Equal(DeviceCategory.Audio, profile.Category);
    }

    [Fact]
    public void Major_class_0x05_classifies_as_Hid()
    {
        // CoD: bits 12-8 = 0x05 (Peripheral), minor=0x04 (keyboard)
        uint cod = 0x0500 | 0x0040;
        var profile = _classifier.Classify(isBle: true, isClassic: false, cod);
        Assert.Equal(DeviceCategory.Hid, profile.Category);
    }

    [Fact]
    public void Major_class_0x01_classifies_as_Unknown_category()
    {
        // CoD: bits 12-8 = 0x01 (Computer) — not in AllowedCategories
        uint cod = 0x0100;
        var profile = _classifier.Classify(isBle: false, isClassic: true, cod);
        Assert.Equal(DeviceCategory.Unknown, profile.Category);
    }

    [Fact]
    public void Null_CoD_classifies_as_Unknown_category()
    {
        var profile = _classifier.Classify(isBle: true, isClassic: false, null);
        Assert.Equal(DeviceCategory.Unknown, profile.Category);
    }

    [Fact]
    public void CoD_major_extraction_ignores_minor_and_format_bits()
    {
        // CoD = 0x240420: major=0x04 (bits 12-8), minor and service bits vary
        uint cod = 0x240420;
        // Major class = (0x240420 >> 8) & 0x1F = 0x0404 & 0x1F = 0x04
        var profile = _classifier.Classify(isBle: false, isClassic: true, cod);
        Assert.Equal(DeviceCategory.Audio, profile.Category);
    }
}

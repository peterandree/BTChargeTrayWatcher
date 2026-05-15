using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class DeviceProfileClassifierTests
{
    private readonly DeviceProfileClassifier _classifier = new();

    // ── Transport classification ────────────────────────────────────────────────

    [Fact]
    public void BLE_only_returns_Ble_transport()
    {
        var (transport, _) = _classifier.Classify(isBle: true, isClassic: false, classOfDevice: null);
        Assert.Equal(DeviceTransport.Ble, transport);
    }

    [Fact]
    public void Classic_only_returns_Classic_transport()
    {
        var (transport, _) = _classifier.Classify(isBle: false, isClassic: true, classOfDevice: null);
        Assert.Equal(DeviceTransport.Classic, transport);
    }

    [Fact]
    public void Both_BLE_and_Classic_returns_DualMode_transport()
    {
        var (transport, _) = _classifier.Classify(isBle: true, isClassic: true, classOfDevice: null);
        Assert.Equal(DeviceTransport.DualMode, transport);
    }

    [Fact]
    public void Neither_BLE_nor_Classic_returns_Unknown_transport()
    {
        var (transport, _) = _classifier.Classify(isBle: false, isClassic: false, classOfDevice: null);
        Assert.Equal(DeviceTransport.Unknown, transport);
    }

    // ── Category classification from Class of Device ────────────────────────────

    [Fact]
    public void Null_classOfDevice_returns_Unknown_category()
    {
        var (_, category) = _classifier.Classify(isBle: true, isClassic: false, classOfDevice: null);
        Assert.Equal(DeviceCategory.Unknown, category);
    }

    [Fact]
    public void Major_class_0x04_returns_Audio_category()
    {
        // Audio/Video: Major Device Class = 0x04, bits 12-8 = 0b00100
        // CoD with major class 4: 0x04 << 8 = 0x0400
        uint cod = 0x04 << 8;
        var (_, category) = _classifier.Classify(isBle: false, isClassic: true, classOfDevice: cod);
        Assert.Equal(DeviceCategory.Audio, category);
    }

    [Fact]
    public void Major_class_0x05_returns_Hid_category()
    {
        // Peripheral: Major Device Class = 0x05
        uint cod = 0x05 << 8;
        var (_, category) = _classifier.Classify(isBle: true, isClassic: false, classOfDevice: cod);
        Assert.Equal(DeviceCategory.Hid, category);
    }

    [Fact]
    public void Major_class_0x01_returns_Unknown_category()
    {
        // Computer: Major Device Class = 0x01 — not a category we track
        uint cod = 0x01 << 8;
        var (_, category) = _classifier.Classify(isBle: false, isClassic: true, classOfDevice: cod);
        Assert.Equal(DeviceCategory.Unknown, category);
    }

    [Fact]
    public void CoD_with_minor_and_service_class_bits_still_extracts_major_correctly()
    {
        // Full CoD: service class (bits 23-13) + major 0x04 (bits 12-8) + minor (bits 7-2)
        // 0x200408 = service bits set | major 0x04 | minor bits
        uint cod = 0x200408;
        var (_, category) = _classifier.Classify(isBle: false, isClassic: true, classOfDevice: cod);
        Assert.Equal(DeviceCategory.Audio, category);
    }

    // ── Combined transport + category ───────────────────────────────────────────

    [Fact]
    public void BLE_audio_device_classified_correctly()
    {
        uint audioCod = 0x04 << 8;
        var (transport, category) = _classifier.Classify(isBle: true, isClassic: false, classOfDevice: audioCod);
        Assert.Equal(DeviceTransport.Ble, transport);
        Assert.Equal(DeviceCategory.Audio, category);
    }

    [Fact]
    public void DualMode_HID_device_classified_correctly()
    {
        uint hidCod = 0x05 << 8;
        var (transport, category) = _classifier.Classify(isBle: true, isClassic: true, classOfDevice: hidCod);
        Assert.Equal(DeviceTransport.DualMode, transport);
        Assert.Equal(DeviceCategory.Hid, category);
    }
}

using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormSaveLoadTests
    {
        [Fact]
        public void SettingsSnapshot_roundtrip_preserves_alias_poll_and_thresholds()
        {
            var s1 = new ThresholdSettings();
            string deviceId = "dev-123";

            s1.SetDisplayNameAlias(deviceId, "My Headset");
            s1.SetPollIntervalForDevice(deviceId, 123);
            s1.SetLowForDevice(deviceId, 18);
            s1.SetHighForDevice(deviceId, 88);

            var snap = s1.Snapshot();

            var s2 = new ThresholdSettings();
            s2.ApplySnapshot(snap);

            Assert.Equal("My Headset", s2.GetDisplayName(deviceId, "fallback"));
            Assert.Equal(123, s2.GetPollIntervalForDevice(deviceId, "fallback"));
            Assert.Equal(18, s2.GetLowForDevice(deviceId, "fallback"));
            Assert.Equal(88, s2.GetHighForDevice(deviceId, "fallback"));
        }
    }
}

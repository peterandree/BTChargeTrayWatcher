using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class NtfyIntegrationSettingsTests
{
    [Fact]
    public void IsConfigured_false_when_topic_is_null()
    {
        var s = new NtfyIntegrationSettings();
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public void IsConfigured_false_when_topic_is_empty()
    {
        var s = new NtfyIntegrationSettings { Topic = "" };
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public void IsConfigured_false_when_topic_is_whitespace()
    {
        var s = new NtfyIntegrationSettings { Topic = "   " };
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public void IsConfigured_true_when_topic_has_value()
    {
        var s = new NtfyIntegrationSettings { Topic = "btcw-abc123" };
        Assert.True(s.IsConfigured);
    }

    [Fact]
    public void Clone_returns_independent_copy()
    {
        var original = new NtfyIntegrationSettings { Topic = "btcw-original", IsEnabled = true };
        var clone = original.Clone();

        clone.Topic = "btcw-modified";
        clone.IsEnabled = false;

        Assert.Equal("btcw-original", original.Topic);
        Assert.True(original.IsEnabled);
    }

    [Fact]
    public void Clone_copies_all_values()
    {
        var original = new NtfyIntegrationSettings { Topic = "btcw-xyz", IsEnabled = true };
        var clone = original.Clone();

        Assert.Equal(original.Topic, clone.Topic);
        Assert.Equal(original.IsEnabled, clone.IsEnabled);
    }
}

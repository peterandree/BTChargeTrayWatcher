using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class ThresholdSettingsSuppressionTests
{
    [Fact]
    public void Suppress_and_Unsuppress_alias_suggestion_affects_query()
    {
        var s = new ThresholdSettings();
        const string id = "device-123";
        Assert.False(s.IsAliasSuggestionSuppressed(id));
        s.SuppressAliasSuggestion(id);
        Assert.True(s.IsAliasSuggestionSuppressed(id));
        s.UnsuppressAliasSuggestion(id);
        Assert.False(s.IsAliasSuggestionSuppressed(id));
    }
}

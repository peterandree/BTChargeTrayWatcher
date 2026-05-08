using System.Collections.Generic;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class NtfyTopicGeneratorTests
{
    [Fact]
    public void Generated_topic_starts_with_btcw_prefix()
    {
        string topic = NtfyTopicGenerator.Generate();
        Assert.StartsWith("btcw-", topic);
    }

    [Fact]
    public void Generated_topic_has_correct_total_length()
    {
        // "btcw-" (5) + 16 hex chars = 21
        string topic = NtfyTopicGenerator.Generate();
        Assert.Equal(21, topic.Length);
    }

    [Fact]
    public void Generated_topic_hex_part_contains_only_lowercase_hex_chars()
    {
        string hex = NtfyTopicGenerator.Generate()[5..];
        foreach (char c in hex)
            Assert.True("0123456789abcdef".Contains(c),
                $"Unexpected character '{c}' in hex part");
    }

    [Fact]
    public void Generated_topics_are_unique_across_many_calls()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
            Assert.True(seen.Add(NtfyTopicGenerator.Generate()),
                "Duplicate topic generated");
    }
}

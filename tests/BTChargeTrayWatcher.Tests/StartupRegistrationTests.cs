using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void IsExecutableCommandMatch_accepts_quoted_executable_only()
    {
        var command = "\"C:\\Apps\\BTChargeTrayWatcher\\BTChargeTrayWatcher.exe\"";

        bool match = StartupRegistration.IsExecutableCommandMatch(
            command,
            "C:\\Apps\\BTChargeTrayWatcher\\BTChargeTrayWatcher.exe");

        Assert.True(match);
    }

    [Fact]
    public void IsExecutableCommandMatch_accepts_command_with_arguments()
    {
        var command = "\"C:\\Apps\\BTChargeTrayWatcher\\BTChargeTrayWatcher.exe\" --minimized --silent";

        bool match = StartupRegistration.IsExecutableCommandMatch(
            command,
            "C:\\Apps\\BTChargeTrayWatcher\\BTChargeTrayWatcher.exe");

        Assert.True(match);
    }

    [Fact]
    public void IsExecutableCommandMatch_accepts_unquoted_command_with_arguments()
    {
        var command = "C:\\Apps\\BTChargeTrayWatcher\\BTChargeTrayWatcher.exe --minimized";

        bool match = StartupRegistration.IsExecutableCommandMatch(
            command,
            "C:\\Apps\\BTChargeTrayWatcher\\BTChargeTrayWatcher.exe");

        Assert.True(match);
    }

    [Fact]
    public void IsExecutableCommandMatch_rejects_different_executable()
    {
        var command = "\"C:\\Apps\\AnotherApp\\AnotherApp.exe\"";

        bool match = StartupRegistration.IsExecutableCommandMatch(
            command,
            "C:\\Apps\\BTChargeTrayWatcher\\BTChargeTrayWatcher.exe");

        Assert.False(match);
    }

    [Theory]
    [InlineData(new byte[] { 0x03 }, true)]
    [InlineData(new byte[] { 0x06 }, true)]
    [InlineData(new byte[] { 0x02 }, false)]
    [InlineData(new byte[] { 0x00 }, false)]
    [InlineData(new byte[0], false)]
    public void IsStartupApprovedDisabled_decodes_known_states(byte[] value, bool expected)
    {
        bool disabled = StartupRegistration.IsStartupApprovedDisabled(value);

        Assert.Equal(expected, disabled);
    }

    [Fact]
    public void IsStartupApprovedDisabled_handles_null_as_not_disabled()
    {
        bool disabled = StartupRegistration.IsStartupApprovedDisabled(null);

        Assert.False(disabled);
    }
}

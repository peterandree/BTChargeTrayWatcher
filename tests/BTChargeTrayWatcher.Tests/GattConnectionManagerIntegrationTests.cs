// GattConnectionManager integration tests require live BLE hardware and
// a WinRT environment — they cannot run in a headless CI environment.
// Concurrency, timeout, and cancellation are exercised at the
// GattBatteryReader level via the injectable test-processor override
// (see GattBatteryReaderTests.cs).

using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class GattConnectionManagerIntegrationTests
{
    [Fact]
    public void Placeholder_compiles() => Assert.True(true);
}


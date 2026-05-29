using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class ClassicBatteryReaderTests
{




    [Fact]
    public async Task Returns_Empty_When_No_Devices()
    {
        var reader = CreateReader(new List<ClassicBluetoothCandidate>(), new HashSet<string>(), new());
        var result = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Returns_Only_Connected_Devices()
    {
        var candidates = new List<ClassicBluetoothCandidate>
        {
            CreateCandidate("A", "id1", 1UL),
            CreateCandidate("B", "id2", 2UL)
        };
        var connected = new HashSet<string> { "1" };
        var batteryMap = new Dictionary<string, (int, bool?)> { ["id1"] = (55, true) };
        var reader = CreateReader(candidates, connected, batteryMap);
        var result = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("A", result[0].Name);
        Assert.Equal(55, result[0].Battery);
        Assert.True(result[0].IsCharging);
    }

    [Fact]
    public async Task Skips_Devices_With_Invalid_Battery()
    {
        var candidates = new List<ClassicBluetoothCandidate>
        {
            CreateCandidate("A", "id1", 1UL),
            CreateCandidate("B", "id2", 2UL)
        };
        var connected = new HashSet<string> { "1", "2" };
        var batteryMap = new Dictionary<string, (int, bool?)>
        {
            ["id1"] = (101, false), // invalid
            ["id2"] = (50, null)
        };
        var reader = CreateReader(candidates, connected, batteryMap);
        var result = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("B", result[0].Name);
        Assert.Equal(50, result[0].Battery);
    }




    // Test double for IClassicBluetoothDeviceEnumerator
    private class FakeEnumerator : IClassicBluetoothDeviceEnumerator
    {
        private readonly List<ClassicBluetoothCandidate> _candidates;
        public FakeEnumerator(List<ClassicBluetoothCandidate> candidates) => _candidates = candidates;
        public List<ClassicBluetoothCandidate> EnumerateCandidates() => _candidates;
    }

    // Test double for IClassicBluetoothConnectionChecker
    private class FakeConnectionChecker : IClassicBluetoothConnectionChecker
    {
        private readonly HashSet<string> _connected;
        public FakeConnectionChecker(HashSet<string> connected) => _connected = connected;
        public Task<bool> IsConnectedAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
        {
            // For test, use address as string for lookup
            return Task.FromResult(_connected.Contains(bluetoothAddress.ToString()));
        }
    }

    // Test double for IClassicBatteryPropertyReader
    private class FakeBatteryPropertyReader : IClassicBatteryPropertyReader
    {
        private readonly Dictionary<string, (int, bool?)> _map;
        public FakeBatteryPropertyReader(Dictionary<string, (int, bool?)> map) => _map = map;
        public Dictionary<string, (int Battery, bool? IsCharging)> ReadBatteryProperties(IEnumerable<string> instanceIds)
        {
            var result = new Dictionary<string, (int, bool?)>();
            foreach (var id in instanceIds)
                if (_map.TryGetValue(id, out var val))
                    result[id] = val;
            return result;
        }
    }

    private static ClassicBluetoothCandidate CreateCandidate(string name, string instanceId, ulong address)
        => new(name, instanceId, address);

    private static ClassicBatteryReader CreateReader(
        List<ClassicBluetoothCandidate> candidates,
        HashSet<string> connected,
        Dictionary<string, (int, bool?)> batteryMap)
    {
        var enumerator = new FakeEnumerator(candidates);
        var connectionChecker = new FakeConnectionChecker(connected);
        var batteryPropertyReader = new FakeBatteryPropertyReader(batteryMap);
        return new ClassicBatteryReader(enumerator, connectionChecker, batteryPropertyReader);
    }

}

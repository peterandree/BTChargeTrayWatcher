namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor
{
    public Task<List<(string Name, int Battery)>> ScanNowAsync() =>
        ScanNowAsync(_shutdownCts.Token);

    public async Task<List<(string Name, int Battery)>> ScanNowAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        _isScanning = true;
        ScanStarted?.Invoke();

        List<(string, int)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (name, battery) in await _gattReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(name)) continue;
                DeviceFound?.Invoke(name, battery);
                results.Add((name, battery));
            }

            foreach (var (name, battery) in await _classicReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(name)) continue;
                DeviceFound?.Invoke(name, battery);
                results.Add((name, battery));
            }
        }
        finally
        {
            _isScanning = false;
            ScanCompleted?.Invoke(results);
        }

        return results;
    }

    public Task<List<(string Name, int Battery)>> StartTrackedScanAsync() =>
        StartTrackedScanAsync(_shutdownCts.Token);

    public Task<List<(string Name, int Battery)>> StartTrackedScanAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        CancellationToken token = linkedCts.Token;

        Task<List<(string Name, int Battery)>> task = ScanNowAsync(token);

        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed)
            {
                linkedCts.Dispose();
                throw new ObjectDisposedException(nameof(BluetoothBatteryMonitor));
            }

            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_taskGate)
            {
                _activeTasks.Remove(t);
            }

            linkedCts.Dispose();

            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Scan task fault: {t.Exception}");
        }, TaskScheduler.Default);

        return task;
    }

    private async Task<List<(string Name, int Battery)>> QuietReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<(string, int)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, battery) in await _gattReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(name)) continue;
            results.Add((name, battery));
        }

        foreach (var (name, battery) in await _classicReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(name)) continue;
            results.Add((name, battery));
        }

        return results;
    }
}

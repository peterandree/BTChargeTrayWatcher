using System.Diagnostics;
using System.Text.Json;

namespace BTChargeTrayWatcher.Monitoring.Logging;

/// <summary>
/// Structured view of one discovery log entry, as passed to the
/// <see cref="DiscoveryLogger.Capture"/> test seam. Mirrors the JSON fields written by
/// <see cref="DiscoveryLogger.Log"/>.
/// </summary>
internal sealed record DiscoveryLogEntry(
    string Reader,
    string Operation,
    string Outcome,
    int ErrorCode,
    string? Message,
    string? DeviceId,
    string? DeviceName,
    int? DurationMs);

/// <summary>
/// Centralized structured discovery logger (ADR-018).
/// Default sink: <see cref="Debug.WriteLine"/> with a compact JSON payload.
/// Optional file sink (rotating, 1 MB cap) is enabled only when the
/// <c>BTCTW_DEV_LOG</c> compile-time symbol is defined — never in production.
/// All logs are local-only; no telemetry or remote transmission.
/// </summary>
internal static class DiscoveryLogger
{
    // ── Error code namespace (ADR-018 §3) ────────────────────────────────────
    internal static class Codes
    {
        internal const int GattTimeout          = 1000;
        internal const int GattDisconnected     = 1001;
        internal const int GattSubscribed       = 1010;
        internal const int GattNotificationReceived = 1011;
        internal const int GattSubscriptionDropped  = 1012;
        internal const int ClassicSetupApiFault = 2000;
        internal const int ClassicPropertyMissing = 2001;
        internal const int EnumerationAccessDenied = 3000;
        internal const int MappingAmbiguous     = 4000;
    }

    // ── Test seam ────────────────────────────────────────────────────────────

    private static readonly AsyncLocal<Action<DiscoveryLogEntry>?> _observer = new();

    /// <summary>
    /// Captures every entry logged inside the current async flow. Production never sets this;
    /// it exists so the ADR-018 logging contract of the GATT subscription policy (#162) can be
    /// asserted without parsing <see cref="Debug.WriteLine"/> output.
    /// </summary>
    internal static IDisposable Capture(Action<DiscoveryLogEntry> sink)
    {
        Action<DiscoveryLogEntry>? previous = _observer.Value;
        _observer.Value = sink;
        return new CaptureScope(previous);
    }

    private sealed class CaptureScope(Action<DiscoveryLogEntry>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _observer.Value = previous;
        }
    }

    // ── File sink (developer-only) ────────────────────────────────────────────
#if BTCTW_DEV_LOG
    private static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BTChargeTrayWatcher",
        "logs");

    private static readonly string _logPath;
    private static readonly object _fileLock = new();
    private const long MaxFileSizeBytes = 1 * 1024 * 1024; // 1 MB

    static DiscoveryLogger()
    {
        Directory.CreateDirectory(_logDir);
        _logPath = Path.Combine(_logDir, "discovery.log");
    }
#endif

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Logs a structured discovery event.
    /// </summary>
    /// <param name="reader">Name of the reader/component emitting the log (e.g. "BatteryReaderOrchestrator").</param>
    /// <param name="operation">Operation name (e.g. "ReadBattery", "SkipSleeping").</param>
    /// <param name="outcome">"OK", "WARN", or "ERROR".</param>
    /// <param name="errorCode">Numeric code from <see cref="Codes"/>, or 0 for no error.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <param name="deviceId">Optional device ID.</param>
    /// <param name="deviceName">Optional device name.</param>
    /// <param name="durationMs">Optional elapsed milliseconds.</param>
    internal static void Log(
        string reader,
        string operation,
        string outcome,
        int errorCode = 0,
        string? message = null,
        string? deviceId = null,
        string? deviceName = null,
        int? durationMs = null)
    {
        var entry = new
        {
            timestamp  = DateTime.UtcNow.ToString("o"),
            reader,
            operation,
            deviceId,
            deviceName,
            durationMs,
            outcome,
            errorCode,
            message
        };

        if (_observer.Value is { } sink)
        {
            try
            {
                sink(new DiscoveryLogEntry(
                    Reader: reader,
                    Operation: operation,
                    Outcome: outcome,
                    ErrorCode: errorCode,
                    Message: message,
                    DeviceId: deviceId,
                    DeviceName: deviceName,
                    DurationMs: durationMs));
            }
            catch (Exception ex)
            {
                // A faulty observer must never break the logging path itself.
                Debug.WriteLine($"[DiscoveryLogger] Observer fault: {ex.Message}");
            }
        }

        string json = JsonSerializer.Serialize(entry);
        Debug.WriteLine(json);

#if BTCTW_DEV_LOG
        WriteToFile(json);
#endif
    }

#if BTCTW_DEV_LOG
    private static void WriteToFile(string line)
    {
        lock (_fileLock)
        {
            try
            {
                // Rotate when file exceeds size cap
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length >= MaxFileSizeBytes)
                {
                    string rotated = Path.ChangeExtension(_logPath, null) + ".1.log";
                    File.Move(_logPath, rotated, overwrite: true);
                }

                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // Best-effort; never throw from a logging path
            }
        }
    }
#endif
}

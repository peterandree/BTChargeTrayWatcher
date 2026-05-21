using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BTChargeTrayWatcher;

/// <summary>
/// Centralized discovery and aggregation logger (ADR-018).
/// Emits structured, compact JSON log lines to <see cref="Debug.WriteLine"/> by default.
/// Optionally writes to a size-capped rolling file in
/// <c>%LOCALAPPDATA%\BTChargeTrayWatcher\discovery.log</c>.
/// </summary>
/// <remarks>
/// <b>Local-only:</b> no network I/O, no remote endpoints. AGENTS.md boundary.
/// File writes are fire-and-forget via <see cref="ThreadPool"/>; never block callers.
/// </remarks>
internal sealed class DiscoveryLogger
{
    // ── Event codes ────────────────────────────────────────────────────────────────
    public const string GattFault      = "GATT_FAULT";
    public const string ClassicFault   = "CLASSIC_FAULT";
    public const string DeviceSkipped  = "DEVICE_SKIPPED";
    public const string ReaderFault    = "READER_FAULT";

    // ── File sink config ──────────────────────────────────────────────────────────
    private const long MaxFileSizeBytes = 1 * 1024 * 1024; // 1 MB
    private const string LogFileName    = "discovery.log";
    private const string BackupFileName = "discovery.log.1";
    private static readonly string LogDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BTChargeTrayWatcher");

    private readonly ThresholdSettings? _settings;

    /// <param name="settings">
    /// When provided, <see cref="ThresholdSettings.DiscoveryLogFileSinkEnabled"/> controls
    /// whether log lines are also written to the file sink.
    /// When <c>null</c>, only <see cref="Debug.WriteLine"/> output is produced.
    /// </param>
    public DiscoveryLogger(ThresholdSettings? settings = null)
    {
        _settings = settings;
    }

    // ── Public logging methods ────────────────────────────────────────────────────

    public void LogGattFault(string deviceName, string deviceId, string errorMessage)
        => Write(GattFault, deviceName, deviceId, "GATT", errorMessage);

    public void LogClassicFault(string errorMessage)
        => Write(ClassicFault, null, null, "Classic", errorMessage);

    public void LogDeviceSkipped(string deviceName, string deviceId, string reason)
        => Write(DeviceSkipped, deviceName, deviceId, null, reason);

    public void LogReaderFault(string source, string errorMessage)
        => Write(ReaderFault, null, null, source, errorMessage);

    // ── Core ──────────────────────────────────────────────────────────────────────

    private void Write(
        string eventCode,
        string? deviceName,
        string? deviceId,
        string? source,
        string? errorMessage)
    {
        var line = BuildLine(eventCode, deviceName, deviceId, source, errorMessage);
        Debug.WriteLine(line);

        if (_settings is { DiscoveryLogFileSinkEnabled: true })
            ThreadPool.QueueUserWorkItem(_ => AppendToFile(line));
    }

    private static string BuildLine(
        string eventCode,
        string? deviceName,
        string? deviceId,
        string? source,
        string? errorMessage)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream,
            new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("ts", DateTime.UtcNow.ToString("o"));
        writer.WriteString("ev", eventCode);
        if (deviceName  is not null) writer.WriteString("name",  deviceName);
        if (deviceId    is not null) writer.WriteString("id",    deviceId);
        if (source      is not null) writer.WriteString("src",   source);
        if (errorMessage is not null) writer.WriteString("err",  errorMessage);
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void AppendToFile(string line)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string path   = Path.Combine(LogDir, LogFileName);
            string backup = Path.Combine(LogDir, BackupFileName);

            // Rotate if at/over size cap
            if (File.Exists(path) && new FileInfo(path).Length >= MaxFileSizeBytes)
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }

            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // File I/O failures must never surface to callers.
        }
    }
}


using System.Text.Json;
namespace BTChargeTrayWatcher;

internal sealed class UiSettings : IUiSettings
{
        private const string AppName = "BTChargeTrayWatcher";
        private readonly string _uiSettingsFilePath;
        private Dictionary<string, UiWindowState> _windowStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();


    public UiSettings()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDir = Path.Combine(localAppData, AppName);
        Directory.CreateDirectory(appDir);
        _uiSettingsFilePath = Path.Combine(appDir, "ui-settings.json");
        Load();
    }

        public UiWindowState? GetWindowState(string key)
        {
            lock (_lock)
                return _windowStates.TryGetValue(key, out var s) ? s : null;
        }

        public void SetWindowState(string key, UiWindowState state)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key");
            lock (_lock)
                _windowStates[key] = state ?? throw new ArgumentNullException(nameof(state));
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_uiSettingsFilePath)) return;
                string json = File.ReadAllText(_uiSettingsFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, UiWindowState>>(json);
                if (dict != null) _windowStates = dict;
            }
            catch { /* ignore load errors */ }
        }

        private void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(_windowStates);
                string tmp = _uiSettingsFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _uiSettingsFilePath, overwrite: true);
            }
            catch { /* ignore save errors */ }
        }
}

namespace BTChargeTrayWatcher;

public interface IUiSettings
{
    UiWindowState? GetWindowState(string key);
    void SetWindowState(string key, UiWindowState state);
}

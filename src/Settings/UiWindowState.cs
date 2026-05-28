using System.Collections.Generic;

namespace BTChargeTrayWatcher;

public sealed record UiWindowState
{
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? WindowState { get; init; }
    public List<int>? ColumnWidths { get; init; }
    public int? SelectedTabIndex { get; init; }
    public Dictionary<string, int>? SplitterDistances { get; init; }
}

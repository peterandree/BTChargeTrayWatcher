using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

internal static class FormStateManager
{
    public static void Monitor(
        Form form,
        IUiSettings uiSettings,
        string key,
        DataGridView? grid = null,
        ListView? listView = null,
        TabControl? tabControl = null,
        IEnumerable<SplitContainer>? splitContainers = null)
    {
        if (form == null) throw new ArgumentNullException(nameof(form));
        if (uiSettings == null) throw new ArgumentNullException(nameof(uiSettings));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key");

        try
        {
            // Restore geometry
            var state = uiSettings.GetWindowState(key);
            if (state != null)
            {
                // Restore columns first so layout math can use them
                if (grid != null && state.ColumnWidths != null && state.ColumnWidths.Count == grid.Columns.Count)
                {
                    for (int i = 0; i < grid.Columns.Count; i++)
                        grid.Columns[i].Width = Math.Max(0, state.ColumnWidths[i]);
                }
                if (listView != null && state.ColumnWidths != null && state.ColumnWidths.Count == listView.Columns.Count)
                {
                    for (int i = 0; i < listView.Columns.Count; i++)
                        listView.Columns[i].Width = Math.Max(0, state.ColumnWidths[i]);
                }

                if (state.Width.HasValue && state.Height.HasValue)
                {
                    var rc = new Rectangle(state.X ?? form.Left, state.Y ?? form.Top, state.Width.Value, state.Height.Value);
                    // Ensure the center point is on a visible screen
                    var center = new Point(rc.Left + rc.Width / 2, rc.Top + rc.Height / 2);
                    bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.Contains(center));
                    if (onScreen)
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Bounds = rc;
                    }
                }

                if (!string.IsNullOrEmpty(state.WindowState) && string.Equals(state.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase))
                    form.WindowState = FormWindowState.Maximized;
                else
                    form.WindowState = FormWindowState.Normal;

                if (tabControl != null && state.SelectedTabIndex.HasValue && state.SelectedTabIndex.Value >= 0 && state.SelectedTabIndex.Value < tabControl.TabCount)
                    tabControl.SelectedIndex = state.SelectedTabIndex.Value;

                if (splitContainers != null && state.SplitterDistances != null)
                {
                    foreach (var sc in splitContainers)
                    {
                        if (string.IsNullOrWhiteSpace(sc.Name)) continue;
                        if (state.SplitterDistances.TryGetValue(sc.Name, out var dist))
                            sc.SplitterDistance = Math.Max(0, Math.Min(dist, sc.Width - sc.Panel2MinSize));
                    }
                }
            }
        }
        catch { /* swallow restoration errors */ }

        void Save()
        {
            try
            {
                Rectangle bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
                var dto = new UiWindowState
                {
                    X = bounds.Left,
                    Y = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    WindowState = form.WindowState == FormWindowState.Maximized ? "Maximized" : "Normal",
                    ColumnWidths = null,
                    SelectedTabIndex = null,
                    SplitterDistances = null
                };

                if (grid != null)
                {
                    dto = dto with { ColumnWidths = grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToList() };
                }
                else if (listView != null)
                {
                    dto = dto with { ColumnWidths = listView.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToList() };
                }

                if (tabControl != null) dto = dto with { SelectedTabIndex = tabControl.SelectedIndex };

                if (splitContainers != null)
                {
                    var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sc in splitContainers)
                    {
                        if (string.IsNullOrWhiteSpace(sc.Name)) continue;
                        dict[sc.Name] = sc.SplitterDistance;
                    }
                    if (dict.Count > 0) dto = dto with { SplitterDistances = dict };
                }

                uiSettings.SetWindowState(key, dto);
            }
            catch { /* swallow save errors */ }
        }

        // Save on close and when the user finishes resizing
        form.FormClosed += (_, _) => Save();
        form.ResizeEnd += (_, _) => Save();
    }
}

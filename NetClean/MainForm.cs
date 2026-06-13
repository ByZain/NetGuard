using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetClean;

internal sealed class MainForm : Form
{
    private const string AppName = "NetGuard";
    private readonly NetworkMonitor _monitor = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly DataGridView _grid = new();
    private readonly Label _statusLabel = new();
    private readonly Label _summaryLabel = new();
    private readonly TextBox _filterBox = new();
    private readonly Button _deepButton = new();
    private readonly Button _limitButton = new();
    private readonly Button _clearLimitButton = new();
    private readonly Button _adminButton = new();
    private readonly Button _copyPathButton = new();
    private List<TrafficRow> _currentRows = [];
    private Dictionary<string, UploadLimitInfo> _uploadLimits = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextLimitRefreshUtc = DateTime.MinValue;
    private bool _isRefreshingLimits;
    private string _sortProperty = nameof(TrafficRow.UploadBytesPerSecond);
    private SortOrder _sortOrder = SortOrder.Descending;

    public MainForm()
    {
        Text = $"{AppName} - 流量查看器";
        MinimumSize = new Size(940, 560);
        Size = new Size(1120, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        BuildLayout();
        WireEvents();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TryStartMonitor();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _monitor.Dispose();
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            Text = "谁在偷偷上传",
            Margin = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(title, 0, 0);

        var toolbar = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10)
        };
        root.Controls.Add(toolbar, 0, 1);

        _filterBox.Width = 240;
        _filterBox.PlaceholderText = "搜索程序名、PID 或路径";
        toolbar.Controls.Add(_filterBox);

        _deepButton.Text = "深度关闭";
        _deepButton.AutoSize = true;
        toolbar.Controls.Add(_deepButton);

        _limitButton.Text = "限制上传";
        _limitButton.AutoSize = true;
        toolbar.Controls.Add(_limitButton);

        _clearLimitButton.Text = "取消限速";
        _clearLimitButton.AutoSize = true;
        toolbar.Controls.Add(_clearLimitButton);

        _copyPathButton.Text = "复制路径";
        _copyPathButton.AutoSize = true;
        toolbar.Controls.Add(_copyPathButton);

        _adminButton.Text = "以管理员重启";
        _adminButton.AutoSize = true;
        _adminButton.Visible = TraceEventSession.IsElevated() != true;
        toolbar.Controls.Add(_adminButton);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 2);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Controls.Add(footer, 0, 3);

        _statusLabel.AutoSize = true;
        _statusLabel.Text = "准备启动监控。";
        _statusLabel.ForeColor = Color.DimGray;
        footer.Controls.Add(_statusLabel, 0, 0);

        _summaryLabel.AutoSize = true;
        _summaryLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _summaryLabel.Text = "当前上传：0 B/s";
        footer.Controls.Add(_summaryLabel, 1, 0);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ShowCellToolTips = false;

        _grid.Columns.Add(CreateTextColumn("Program", "程序", 190));
        _grid.Columns.Add(CreateTextColumn("ProcessTag", "标记", 90));
        _grid.Columns.Add(CreateTextColumn("Status", "状态", 80));
        _grid.Columns.Add(CreateTextColumn("UploadLimit", "限速", 90));
        _grid.Columns.Add(CreateTextColumn("Pid", "PID", 70, DataGridViewContentAlignment.MiddleRight));
        _grid.Columns.Add(CreateTextColumn("UploadSpeed", "上传速度", 115, DataGridViewContentAlignment.MiddleRight, nameof(TrafficRow.UploadBytesPerSecond)));
        _grid.Columns.Add(CreateTextColumn("DownloadSpeed", "下载速度", 115, DataGridViewContentAlignment.MiddleRight, nameof(TrafficRow.DownloadBytesPerSecond)));
        _grid.Columns.Add(CreateTextColumn("TotalUpload", "累计上传", 115, DataGridViewContentAlignment.MiddleRight, nameof(TrafficRow.TotalUploadBytes)));
        _grid.Columns.Add(CreateTextColumn("LastSeen", "最后活动", 115, DataGridViewContentAlignment.MiddleLeft, nameof(TrafficRow.LastSeenAt)));
        _grid.Columns.Add(CreateTextColumn("Path", "路径", 360));

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not TrafficRow row)
            {
                return;
            }

            _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = row.IsCriticalSystemProcess || !row.IsRunning
                ? Color.DimGray
                : SystemColors.ControlText;

            if (!row.IsRunning)
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            }
            else if (row.IsUploadLimited)
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(232, 248, 238);
            }
            else if (row.UploadBytesPerSecond >= 1024 * 1024)
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 238, 238);
            }
            else if (row.UploadBytesPerSecond >= 128 * 1024)
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 225);
            }
            else if (row.IsCriticalSystemProcess)
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(240, 242, 245);
            }
            else if (row.IsSystemProcess)
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(238, 246, 255);
            }
            else
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = SystemColors.Window;
            }
        };
    }

    private static DataGridViewTextBoxColumn CreateTextColumn(
        string propertyName,
        string header,
        int width,
        DataGridViewContentAlignment alignment = DataGridViewContentAlignment.MiddleLeft,
        string? sortProperty = null)
    {
        return new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = header,
            FillWeight = width,
            MinimumWidth = Math.Min(width, 90),
            SortMode = DataGridViewColumnSortMode.Programmatic,
            Tag = sortProperty ?? propertyName,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment }
        };
    }

    private void WireEvents()
    {
        _monitor.StatusChanged += message =>
        {
            if (IsDisposed)
            {
                return;
            }

            BeginInvoke(() => SetStatus(message, message.Contains("停止", StringComparison.Ordinal) ? Color.Firebrick : Color.DimGray));
        };

        _refreshTimer.Interval = 1000;
        _refreshTimer.Tick += (_, _) => RefreshRows();

        _filterBox.TextChanged += (_, _) => ApplyGridDataSource();
        _deepButton.Click += async (_, _) => await DeepStopSelectedProcessAsync();
        _limitButton.Click += async (_, _) => await LimitSelectedUploadAsync();
        _clearLimitButton.Click += async (_, _) => await ClearSelectedUploadLimitAsync();
        _copyPathButton.Click += (_, _) => CopySelectedPath();
        _adminButton.Click += (_, _) => RestartAsAdministrator();
        _grid.ColumnHeaderMouseClick += (_, e) => SortByColumn(_grid.Columns[e.ColumnIndex]);
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                await DeepStopSelectedProcessAsync();
            }
        };
    }

    private void TryStartMonitor()
    {
        try
        {
            _monitor.Start();
            _refreshTimer.Start();
            SetStatus("正在监控。点击列标题可排序。", Color.DimGray);
        }
        catch (Exception ex)
        {
            _refreshTimer.Stop();
            SetStatus(ex.Message, Color.Firebrick);
            _adminButton.Visible = true;
        }
    }

    private void RefreshRows()
    {
        RefreshUploadLimitCacheIfDue();
        var snapshots = _monitor.ReadSnapshots();
        _currentRows = snapshots.Select(snapshot =>
        {
            var runState = ProcessInspector.GetRunState(snapshot.ProcessId);
            var uploadLimit = GetUploadLimit(snapshot.Path);
            return new TrafficRow
            {
                Status = runState.Status,
                Pid = snapshot.ProcessId,
                Program = snapshot.DisplayName,
                ProcessName = snapshot.ProcessName,
                ProcessTag = snapshot.ProcessTag,
                UploadLimit = uploadLimit?.DisplayText ?? "",
                UploadSpeed = Formatters.FormatBytesPerSecond(snapshot.UploadBytesPerSecond),
                DownloadSpeed = Formatters.FormatBytesPerSecond(snapshot.DownloadBytesPerSecond),
                TotalUpload = Formatters.FormatBytes(snapshot.TotalUploadBytes),
                LastSeen = snapshot.LastSeen.ToString("HH:mm:ss"),
                Path = snapshot.Path,
                UploadBytesPerSecond = snapshot.UploadBytesPerSecond,
                DownloadBytesPerSecond = snapshot.DownloadBytesPerSecond,
                TotalUploadBytes = snapshot.TotalUploadBytes,
                LastSeenAt = snapshot.LastSeen,
                IsSystemProcess = snapshot.IsSystemProcess,
                IsCriticalSystemProcess = snapshot.IsCriticalSystemProcess,
                IsRunning = runState.IsRunning,
                IsUploadLimited = uploadLimit is not null
            };
        }).ToList();

        var totalUpload = _currentRows.Sum(row => row.UploadBytesPerSecond);
        _summaryLabel.Text = $"当前上传：{Formatters.FormatBytesPerSecond(totalUpload)}";
        ApplyGridDataSource();
    }

    private void ApplyGridDataSource()
    {
        var selectedPid = SelectedRow()?.Pid;
        var filter = _filterBox.Text.Trim();
        IEnumerable<TrafficRow> rows = _currentRows;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = rows.Where(row =>
                row.Pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.ProcessTag.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                row.Status.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                row.UploadLimit.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                row.Program.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                row.ProcessName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                row.Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        }

        _grid.DataSource = SortRows(rows).ToList();
        UpdateSortGlyph();

        if (selectedPid.HasValue)
        {
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                if (gridRow.DataBoundItem is TrafficRow row && row.Pid == selectedPid.Value)
                {
                    gridRow.Selected = true;
                    _grid.CurrentCell = gridRow.Cells[0];
                    break;
                }
            }
        }
    }

    private IEnumerable<TrafficRow> SortRows(IEnumerable<TrafficRow> rows)
    {
        return _sortProperty switch
        {
            nameof(TrafficRow.Pid) => Sort(rows, row => row.Pid),
            nameof(TrafficRow.UploadBytesPerSecond) => Sort(rows, row => row.UploadBytesPerSecond),
            nameof(TrafficRow.DownloadBytesPerSecond) => Sort(rows, row => row.DownloadBytesPerSecond),
            nameof(TrafficRow.TotalUploadBytes) => Sort(rows, row => row.TotalUploadBytes),
            nameof(TrafficRow.LastSeenAt) => Sort(rows, row => row.LastSeenAt),
            nameof(TrafficRow.Path) => Sort(rows, row => row.Path),
            nameof(TrafficRow.ProcessTag) => Sort(rows, row => row.ProcessTag),
            nameof(TrafficRow.Status) => Sort(rows, row => row.Status),
            nameof(TrafficRow.UploadLimit) => Sort(rows, row => row.UploadLimit),
            nameof(TrafficRow.ProcessName) => Sort(rows, row => row.ProcessName),
            _ => Sort(rows, row => row.Program)
        };
    }

    private IEnumerable<TrafficRow> Sort<TKey>(IEnumerable<TrafficRow> rows, Func<TrafficRow, TKey> keySelector)
    {
        return _sortOrder == SortOrder.Ascending
            ? rows.OrderBy(keySelector).ThenBy(row => row.Program)
            : rows.OrderByDescending(keySelector).ThenBy(row => row.Program);
    }

    private void SortByColumn(DataGridViewColumn column)
    {
        if (column.Tag is not string sortProperty)
        {
            return;
        }

        if (_sortProperty == sortProperty)
        {
            _sortOrder = _sortOrder == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
        }
        else
        {
            _sortProperty = sortProperty;
            _sortOrder = IsNumericSort(sortProperty) ? SortOrder.Descending : SortOrder.Ascending;
        }

        ApplyGridDataSource();
    }

    private void UpdateSortGlyph()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = column.Tag as string == _sortProperty ? _sortOrder : SortOrder.None;
        }
    }

    private static bool IsNumericSort(string sortProperty)
    {
        return sortProperty is nameof(TrafficRow.Pid)
            or nameof(TrafficRow.UploadBytesPerSecond)
            or nameof(TrafficRow.DownloadBytesPerSecond)
            or nameof(TrafficRow.TotalUploadBytes)
            or nameof(TrafficRow.LastSeenAt);
    }

    private TrafficRow? SelectedRow()
    {
        if (_grid.CurrentRow?.DataBoundItem is TrafficRow row)
        {
            return row;
        }

        return _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].DataBoundItem as TrafficRow : null;
    }

    private async Task DeepStopSelectedProcessAsync()
    {
        var row = SelectedRow();
        if (row is null)
        {
            MessageBox.Show(this, "先在列表里选中一个程序。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (row.IsCriticalSystemProcess)
        {
            MessageBox.Show(this, "这是系统关键进程，已阻止深度关闭。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var message =
            $"确定要深度关闭“{row.Program}”（PID {row.Pid}）吗？\n\n" +
            "深度关闭会先停止该进程对应的 Windows 服务，然后结束残留进程树。";

        if (row.IsSystemProcess)
        {
            message += $"\n\n注意：它被标记为“{row.ProcessTag}”，不确定用途时建议先别关。";
        }

        if (MessageBox.Show(this, message, "深度关闭", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        SetActionButtonsEnabled(false);
        SetStatus("正在深度关闭，请稍等。", Color.DimGray);

        try
        {
            var (stopResult, relatedText) = await Task.Run(() =>
            {
                var result = ProcessTerminator.TryDeepClose(row.Pid);
                var related = ProcessInspector.FindRelatedProcesses(row.Pid, row.Path);
                return (result, ProcessInspector.FormatRelatedProcesses(related));
            });

            var icon = stopResult.Status switch
            {
                ProcessStopStatus.Failed => MessageBoxIcon.Error,
                ProcessStopStatus.NeedsForce => MessageBoxIcon.Warning,
                _ => MessageBoxIcon.Information
            };

            var report = stopResult.Message;
            if (!string.IsNullOrWhiteSpace(relatedText))
            {
                report += "\n\n" + relatedText;
            }

            MessageBox.Show(this, report, AppName, MessageBoxButtons.OK, icon);
            RefreshRows();
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        _deepButton.Enabled = enabled;
        _limitButton.Enabled = enabled;
        _clearLimitButton.Enabled = enabled;
        _copyPathButton.Enabled = enabled;
        _adminButton.Enabled = enabled;
    }

    private UploadLimitInfo? GetUploadLimit(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (_uploadLimits.TryGetValue(path, out var fullPathLimit))
        {
            return fullPathLimit;
        }

        var appName = Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(appName) && _uploadLimits.TryGetValue(appName, out var appNameLimit)
            ? appNameLimit
            : null;
    }

    private void RefreshUploadLimitCacheIfDue()
    {
        if (_isRefreshingLimits || DateTime.UtcNow < _nextLimitRefreshUtc)
        {
            return;
        }

        _isRefreshingLimits = true;
        _nextLimitRefreshUtc = DateTime.UtcNow.AddSeconds(5);

        _ = Task.Run(UploadLimiter.GetActiveLimits).ContinueWith(task =>
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            _uploadLimits = new Dictionary<string, UploadLimitInfo>(task.Result, StringComparer.OrdinalIgnoreCase);
                            ApplyGridDataSource();
                        }
                    }
                    finally
                    {
                        _isRefreshingLimits = false;
                    }
                });
            }
            catch
            {
                _isRefreshingLimits = false;
            }
        });
    }

    private async Task RefreshUploadLimitCacheNowAsync()
    {
        try
        {
            _uploadLimits = new Dictionary<string, UploadLimitInfo>(
                await Task.Run(UploadLimiter.GetActiveLimits),
                StringComparer.OrdinalIgnoreCase);
            _nextLimitRefreshUtc = DateTime.UtcNow.AddSeconds(5);
        }
        catch
        {
            _nextLimitRefreshUtc = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private async Task LimitSelectedUploadAsync()
    {
        var row = SelectedRow();
        if (row is null)
        {
            MessageBox.Show(this, "先在列表里选中一个程序。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Path))
        {
            MessageBox.Show(this, "选中的程序没有可读取的路径，无法按程序限速。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (row.IsCriticalSystemProcess)
        {
            MessageBox.Show(this, "这是系统关键进程，已阻止限速。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new LimitUploadDialog(row.Program);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetActionButtonsEnabled(false);
        SetStatus("正在创建上传限速策略。", Color.DimGray);

        try
        {
            var result = await Task.Run(() => UploadLimiter.ApplyLimit(row.Path, dialog.KilobytesPerSecond));
            MessageBox.Show(this, result.Message, AppName, MessageBoxButtons.OK, result.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            SetStatus(result.Succeeded ? "上传限速策略已创建。" : "上传限速失败。", result.Succeeded ? Color.DimGray : Color.Firebrick);
            await RefreshUploadLimitCacheNowAsync();
            RefreshRows();
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    private async Task ClearSelectedUploadLimitAsync()
    {
        var row = SelectedRow();
        if (row is null)
        {
            MessageBox.Show(this, "先在列表里选中一个程序。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Path))
        {
            MessageBox.Show(this, "选中的程序没有可读取的路径，无法取消限速。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetActionButtonsEnabled(false);
        SetStatus("正在取消上传限速策略。", Color.DimGray);

        try
        {
            var result = await Task.Run(() => UploadLimiter.ClearLimit(row.Path));
            MessageBox.Show(this, result.Message, AppName, MessageBoxButtons.OK, result.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            SetStatus(result.Succeeded ? "上传限速策略已更新。" : "取消限速失败。", result.Succeeded ? Color.DimGray : Color.Firebrick);
            await RefreshUploadLimitCacheNowAsync();
            RefreshRows();
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    private void CopySelectedPath()
    {
        var row = SelectedRow();
        if (row is null || string.IsNullOrWhiteSpace(row.Path))
        {
            MessageBox.Show(this, "选中的程序没有可读取的路径。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Clipboard.SetText(row.Path);
        SetStatus("路径已复制。", Color.DimGray);
    }

    private void RestartAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("找不到当前程序路径。");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            });

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法以管理员身份重启：{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string message, Color color)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = color;
    }
}

namespace NetClean;

internal sealed class LimitUploadDialog : Form
{
    private readonly NumericUpDown _speedInput = new();

    public LimitUploadDialog(string programName)
    {
        Text = "限制上传";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 170);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 36,
            Text = $"限制“{programName}”的上传速度",
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            AutoEllipsis = true
        };
        root.Controls.Add(title, 0, 0);

        var inputRow = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Margin = new Padding(0, 8, 0, 0)
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(inputRow, 0, 1);

        var label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "上传上限（KB/s）：",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0)
        };
        inputRow.Controls.Add(label, 0, 0);

        _speedInput.Dock = DockStyle.Left;
        _speedInput.Minimum = 1;
        _speedInput.Maximum = 1024 * 1024;
        _speedInput.Value = 128;
        _speedInput.Width = 130;
        _speedInput.ThousandsSeparator = true;
        inputRow.Controls.Add(_speedInput, 1, 0);

        var buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 24, 0, 0)
        };
        root.Controls.Add(buttonRow, 0, 2);

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 30)
        };
        buttonRow.Controls.Add(cancelButton);

        var okButton = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 30)
        };
        buttonRow.Controls.Add(okButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public int KilobytesPerSecond => (int)_speedInput.Value;
}

using PaCodeCopy.Core.Configuration;

namespace PaCodeCopy.Forms;

/// <summary>Edits a copy of <see cref="AppSettings"/>; the caller applies and saves it on OK.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TextBox _folderBox = new() { Width = 380 };
    private readonly TextBox _categoryBox = new() { Width = 200 };
    private readonly NumericUpDown _thresholdBox = new()
    {
        Minimum = AppSettings.MinDetectionThreshold,
        Maximum = AppSettings.MaxDetectionThreshold,
        Width = 70,
    };
    private readonly NumericUpDown _retentionBox = new() { Minimum = 1, Maximum = 365, Width = 70 };
    private readonly CheckBox _autoCaptureBox = new() { Text = "Auto-capture PAD code from the clipboard", AutoSize = true };
    private readonly CheckBox _trayBox = new() { Text = "Minimise to system tray", AutoSize = true };
    private readonly CheckBox _notifyBox = new() { Text = "Show a notification when a snippet is auto-saved", AutoSize = true };

    public SettingsForm(AppSettings current)
    {
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _folderBox.Text = current.SnippetsRootFolder;
        _categoryBox.Text = current.DefaultCategory;
        _thresholdBox.Value = current.DetectionThreshold;
        _retentionBox.Value = current.LogRetentionDays;
        _autoCaptureBox.Checked = current.AutoCapture;
        _trayBox.Checked = current.MinimizeToTray;
        _notifyBox.Checked = current.ShowNotifications;

        var browseButton = new Button { Text = "Browse…", AutoSize = true };
        browseButton.Click += (_, _) => BrowseFolder();

        var folderPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        folderPanel.Controls.Add(_folderBox);
        folderPanel.Controls.Add(browseButton);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(layout, "Snippets &folder:", folderPanel);
        AddRow(layout, "Default &category:", _categoryBox);
        AddRow(layout, "Detection &strictness:", _thresholdBox);
        AddRow(layout, "Keep &logs (days):", _retentionBox);
        AddRow(layout, string.Empty, _autoCaptureBox);
        AddRow(layout, string.Empty, _trayBox);
        AddRow(layout, string.Empty, _notifyBox);

        var hint = new Label
        {
            Text = "Strictness: higher = fewer false captures, lower = catches very small copies (default 3).",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
        };
        AddRow(layout, string.Empty, hint);

        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        AddRow(layout, string.Empty, buttons);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
        okButton.Click += (_, _) => ValidateFolder();
    }

    /// <summary>Returns the edited values as a new settings object.</summary>
    public AppSettings Result => new()
    {
        SnippetsRootFolder = _folderBox.Text.Trim(),
        DefaultCategory = _categoryBox.Text.Trim(),
        DetectionThreshold = (int)_thresholdBox.Value,
        LogRetentionDays = (int)_retentionBox.Value,
        AutoCapture = _autoCaptureBox.Checked,
        MinimizeToTray = _trayBox.Checked,
        ShowNotifications = _notifyBox.Checked,
    };

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where snippets are saved",
            UseDescriptionForTitle = true,
            SelectedPath = _folderBox.Text,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderBox.Text = dialog.SelectedPath;
        }
    }

    private void ValidateFolder()
    {
        var folder = _folderBox.Text.Trim();
        try
        {
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
            {
                throw new ArgumentException("Enter a full folder path, for example C:\\Users\\me\\Documents\\PaSnippets.");
            }

            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, $"The snippets folder cannot be used:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _folderBox.Focus();
        }
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, layout.RowCount);
        layout.Controls.Add(control, 1, layout.RowCount);
        layout.RowCount++;
    }
}

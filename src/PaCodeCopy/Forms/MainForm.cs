using System.Diagnostics;
using System.Globalization;
using PaCodeCopy.Core.Configuration;
using PaCodeCopy.Core.Extraction;
using PaCodeCopy.Core.Logging;
using PaCodeCopy.Core.Models;
using PaCodeCopy.Core.Services;
using PaCodeCopy.Interop;

namespace PaCodeCopy.Forms;

/// <summary>
/// Main window: snippet list with search on the left, code preview on the right,
/// toolbar actions on top, and a tray icon for background capture.
/// </summary>
internal sealed partial class MainForm : Form
{
    private const string AllCategories = "(All categories)";

    private readonly SnippetService _service;
    private readonly SettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private AppSettings _settings;

    // On/off switch. The text and colour show the current state, because a "checked" toolbar button is hard to see.
    private readonly ToolStripButton _autoCaptureButton = new("Auto-capture: ON") { CheckOnClick = true };
    private readonly TextBox _searchBox = new() { Dock = DockStyle.Fill, PlaceholderText = "Search name, tags or code…  (Ctrl+F)" };
    private readonly ComboBox _categoryFilter = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowItemToolTips = true,
    };
    private readonly RichTextBox _preview = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        WordWrap = false,
        Font = new Font("Consolas", 10f),
        BackColor = SystemColors.Window,
        DetectUrls = false,
    };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ToolStripMenuItem _trayAutoCaptureItem = new("Auto-capture") { CheckOnClick = true };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 300 };

    private ClipboardMonitor? _clipboardMonitor;
    // Writing to the clipboard can raise several WM_CLIPBOARDUPDATE messages (e.g. when the data is
    // flushed so it survives app exit), so our own copies are recognised by content, not by counting events.
    private static readonly TimeSpan SelfCopyWindow = TimeSpan.FromSeconds(3);
    private string? _selfCopiedHash;
    private DateTime _selfCopiedAtUtc;
    private bool _updatingToggles;

    public MainForm(SnippetService service, SettingsStore settingsStore, AppSettings settings, IAppLogger logger)
    {
        _service = service;
        _settingsStore = settingsStore;
        _settings = settings;
        _logger = logger;

        Text = "PA CodeCopy — Power Automate Desktop snippets";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 680);
        MinimumSize = new Size(700, 400);
        Font = SystemFonts.MessageBoxFont ?? Font;
        KeyPreview = true;

        BuildLayout();
        BuildTrayIcon();
        WireEvents();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Splitter sizes must be set after the form has its real size, otherwise WinForms clamps them.
        _split.Panel1MinSize = 300;
        _split.SplitterDistance = Math.Max(_split.Panel1MinSize, Math.Min(480, _split.Width - _split.Panel2MinSize));

        StartClipboardMonitor();
        SetAutoCapture(_settings.AutoCapture, save: false);
        ReloadCategories();
        ReloadList();
        SetStatus($"Snippets folder: {_service.FileStore.RootFolder}");
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _extractionCts?.Cancel();
        _clipboardMonitor?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _searchTimer.Dispose();
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.F:
                _searchBox.Focus();
                _searchBox.SelectAll();
                return true;
            case Keys.Control | Keys.Shift | Keys.C:
                CopySelectedToClipboard();
                return true;
            case Keys.Control | Keys.S:
                SaveFromClipboard();
                return true;
            case Keys.F2:
                EditSelected();
                return true;
            case Keys.F5:
                ReloadList();
                return true;
            case Keys.Delete when _list.Focused:
                DeleteSelected();
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(16, 16) };
        toolbar.Items.AddRange(new ToolStripItem[]
        {
            Button("Save from clipboard", "Save the code currently on the clipboard (Ctrl+S)", SaveFromClipboard),
            Button("Paste && Save…", "Paste or type code and save it with a name", PasteAndSave),
            Button("Extract PAD flow", "Copy every subflow of the flow open in the PAD designer and save each one (one click)", ExtractPadFlow),
            new ToolStripSeparator(),
            Button("Copy to clipboard", "Copy the selected snippet so you can paste it into PAD (Ctrl+Shift+C)", CopySelectedToClipboard),
            Button("Edit…", "Rename, re-categorise or tag the selected snippet (F2)", EditSelected),
            Button("Delete", "Delete the selected snippet (Del)", DeleteSelected),
            new ToolStripSeparator(),
            Button("Open folder", "Show the snippet file in Explorer", OpenFolder),
            Button("Rebuild index", "Re-scan the snippets folder and rebuild the search index", RebuildIndex),
            Button("Scan PAD window…", "Troubleshooting: save PAD's window structure to a file", ScanPadWindow),
            new ToolStripSeparator(),
            _autoCaptureButton,
            Button("Settings…", "Change folder, strictness and other options", OpenSettings),
        });

        _list.Columns.Add("Name", 260);
        _list.Columns.Add("Category", 110);
        _list.Columns.Add("Actions", 60, HorizontalAlignment.Right);
        _list.Columns.Add("Last copied", 130);

        var filterPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        filterPanel.Controls.Add(_searchBox, 0, 0);
        filterPanel.Controls.Add(_categoryFilter, 0, 1);

        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_list);
        leftPanel.Controls.Add(filterPanel);

        _split.Panel1.Padding = new Padding(6);
        _split.Panel2.Padding = new Padding(6);
        _split.Panel1.Controls.Add(leftPanel);
        _split.Panel2.Controls.Add(_preview);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(_countLabel);

        Controls.Add(_split);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);
    }

    private void BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PA CodeCopy", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Save from clipboard", null, (_, _) => SaveFromClipboard());
        menu.Items.Add("Extract PAD flow", null, (_, _) => ExtractPadFlow());
        menu.Items.Add(_trayAutoCaptureItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.Text = "PA CodeCopy";
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void WireEvents()
    {
        _autoCaptureButton.CheckedChanged += (_, _) => SetAutoCapture(_autoCaptureButton.Checked, save: true);
        _trayAutoCaptureItem.CheckedChanged += (_, _) => SetAutoCapture(_trayAutoCaptureItem.Checked, save: true);

        _searchBox.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ReloadList();
        };
        _categoryFilter.SelectedIndexChanged += (_, _) => ReloadList();

        _list.SelectedIndexChanged += (_, _) => ShowPreview();
        _list.DoubleClick += (_, _) => CopySelectedToClipboard();
    }

    private static ToolStripButton Button(string text, string tooltip, Action onClick)
    {
        var button = new ToolStripButton(text) { ToolTipText = tooltip, DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------------------------------------------------------- clipboard

    private void StartClipboardMonitor()
    {
        try
        {
            _clipboardMonitor = new ClipboardMonitor();
            _clipboardMonitor.ClipboardChanged += OnClipboardChanged;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.Error("Clipboard monitor could not start; auto-capture is unavailable.", ex);
            _autoCaptureButton.Enabled = false;
            _trayAutoCaptureItem.Enabled = false;
            SetStatus("Auto-capture is unavailable (see log). Use 'Save from clipboard' instead.");
        }
    }

    private async void OnClipboardChanged(object? sender, EventArgs e)
    {
        // During "Extract PAD flow" the extractor saves PAD's copies itself.
        if (!_settings.AutoCapture || IsExtractionBusy)
        {
            return;
        }

        var text = ClipboardHelper.TryGetText(_logger);
        if (text is null || IsOwnCopy(text) || !_service.Analyze(text).IsPadScript)
        {
            return;
        }

        // Save into the folder of the PAD flow the code was copied from (default category otherwise).
        var source = await DetectCopySourceAsync();
        var result = _service.Save(text, SnippetSource.Auto, requirePadScript: true,
            category: source?.FlowName, namePrefix: source?.SubflowName);
        switch (result.Outcome)
        {
            case SaveOutcome.Saved:
                AfterSave(result);
                if (_settings.ShowNotifications && !Visible)
                {
                    _trayIcon.ShowBalloonTip(2000, "Snippet saved",
                        $"{result.Snippet!.Name}\nFolder: {result.Snippet.Category}", ToolTipIcon.Info);
                }

                break;
            case SaveOutcome.Duplicate:
                // One Ctrl+C can raise several clipboard events; keep the "Saved …" message of a fresh save.
                if (DateTime.UtcNow - result.Snippet!.CreatedAt < TimeSpan.FromSeconds(5))
                {
                    break;
                }

                SetStatus(result.Message);
                ReloadList(selectId: result.Snippet.Id);
                break;
            case SaveOutcome.Failed:
                SetStatus(result.Message);
                if (!Visible)
                {
                    _trayIcon.ShowBalloonTip(3000, "Snippet not saved", result.Message, ToolTipIcon.Error);
                }

                break;
            // NotPadScript / Empty: ordinary copies are ignored silently.
        }
    }

    private void SetAutoCapture(bool enabled, bool save)
    {
        // Setting Checked raises CheckedChanged on both toggles; ignore those nested calls.
        if (_updatingToggles)
        {
            return;
        }

        _updatingToggles = true;
        try
        {
            _autoCaptureButton.Checked = enabled;
            _trayAutoCaptureItem.Checked = enabled;
        }
        finally
        {
            _updatingToggles = false;
        }

        _settings.AutoCapture = enabled;
        _trayIcon.Text = enabled ? "PA CodeCopy - auto-capture ON" : "PA CodeCopy - auto-capture OFF";
        _autoCaptureButton.Text = enabled ? "Auto-capture: ON" : "Auto-capture: OFF";
        _autoCaptureButton.ForeColor = enabled ? Color.DarkGreen : Color.Firebrick;
        if (!_autoCaptureButton.Font.Bold)
        {
            _autoCaptureButton.Font = new Font(_autoCaptureButton.Font, FontStyle.Bold);
        }
        _autoCaptureButton.ToolTipText = enabled
            ? "ON: PAD code is saved automatically when you copy it. Click to turn OFF."
            : "OFF: copies are not saved automatically. Click to turn ON.";
        if (save)
        {
            SaveSettings();
            SetStatus(enabled
                ? "Auto-capture is ON. Copy actions in PAD (Ctrl+C) and they are saved automatically."
                : "Auto-capture is OFF. Use 'Save from clipboard' (Ctrl+S) to save manually.");
        }
    }

    // ---------------------------------------------------------------- actions

    private async void SaveFromClipboard()
    {
        var text = ClipboardHelper.TryGetText(_logger);
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("The clipboard has no text.");
            return;
        }

        var detection = _service.Analyze(text);
        if (!detection.IsPadScript)
        {
            var answer = MessageBox.Show(this,
                "The clipboard text does not look like Power Automate Desktop code.\n\nSave it anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        var source = await DetectCopySourceAsync();
        var result = _service.Save(text, SnippetSource.Manual, requirePadScript: false,
            category: source?.FlowName, namePrefix: source?.SubflowName);
        HandleManualResult(result);
    }

    private void PasteAndSave()
    {
        using var dialog = new SnippetEditForm("Paste && Save", _service.GetCategories(), string.Empty,
            _settings.DefaultCategory, string.Empty, showCodeBox: true);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = _service.Save(dialog.Code, SnippetSource.Manual, requirePadScript: false,
            dialog.SnippetName, dialog.Category, dialog.Tags);
        HandleManualResult(result);
    }

    private void HandleManualResult(SaveResult result)
    {
        if (result.Outcome == SaveOutcome.Saved)
        {
            AfterSave(result);
            return;
        }

        if (result.Outcome == SaveOutcome.Duplicate)
        {
            ReloadList(selectId: result.Snippet?.Id);
        }

        SetStatus(result.Message);
        if (result.Outcome == SaveOutcome.Failed)
        {
            MessageBox.Show(this, result.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AfterSave(SaveResult result)
    {
        ReloadCategories();
        ReloadList(selectId: result.Snippet?.Id);
        SetStatus(result.Snippet is { } snippet ? $"Saved '{snippet.Name}' in folder '{snippet.Category}'." : result.Message);
    }

    /// <summary>The PAD flow (and subflow) the clipboard content came from; null when not copied in the PAD designer.</summary>
    private async Task<PadCopySource?> DetectCopySourceAsync()
    {
        try
        {
            // Off the UI thread: the subflow lookup uses UI Automation.
            return await Task.Run(() => PadCopyContext.Detect(_logger));
        }
        catch (Exception ex)
        {
            _logger.Warn("Could not find the PAD flow of the copy; using the default folder.", ex);
            return null;
        }
    }

    private void CopySelectedToClipboard()
    {
        if (SelectedSnippet is not { } snippet)
        {
            return;
        }

        var content = _service.ReadContent(snippet);
        _selfCopiedHash = SnippetService.ComputeHash(content);
        _selfCopiedAtUtc = DateTime.UtcNow;
        if (ClipboardHelper.TrySetText(content, _logger))
        {
            SetStatus($"Copied '{snippet.Name}'. Paste it into the PAD designer with Ctrl+V.");
        }
        else
        {
            _selfCopiedHash = null;
            SetStatus("Could not write to the clipboard. Try again.");
        }
    }

    /// <summary>True when the clipboard holds the text this app copied a moment ago.</summary>
    private bool IsOwnCopy(string text)
    {
        if (_selfCopiedHash is null)
        {
            return false;
        }

        if (DateTime.UtcNow - _selfCopiedAtUtc <= SelfCopyWindow &&
            string.Equals(SnippetService.ComputeHash(text), _selfCopiedHash, StringComparison.Ordinal))
        {
            return true;
        }

        _selfCopiedHash = null;
        return false;
    }

    private void EditSelected()
    {
        if (SelectedSnippet is not { } snippet)
        {
            return;
        }

        using var dialog = new SnippetEditForm("Edit snippet", _service.GetCategories(), snippet.Name,
            snippet.Category, snippet.Tags, showCodeBox: false);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _service.UpdateDetails(snippet, dialog.SnippetName, dialog.Category, dialog.Tags);
            ReloadCategories();
            ReloadList(selectId: snippet.Id);
            SetStatus($"Updated '{snippet.Name}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.Error($"Failed to update snippet #{snippet.Id}.", ex);
            MessageBox.Show(this, $"Could not update the snippet:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            ReloadList(selectId: snippet.Id);
        }
    }

    private void DeleteSelected()
    {
        if (SelectedSnippet is not { } snippet)
        {
            return;
        }

        var answer = MessageBox.Show(this, $"Delete '{snippet.Name}'?\nThe file will be removed from disk.",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _service.Delete(snippet);
            ReloadCategories();
            ReloadList();
            SetStatus($"Deleted '{snippet.Name}'.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _logger.Error($"Failed to delete snippet #{snippet.Id}.", ex);
            MessageBox.Show(this, $"Could not delete the snippet:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenFolder()
    {
        try
        {
            if (SelectedSnippet is { } snippet && File.Exists(snippet.FilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{snippet.FilePath}\"");
                return;
            }

            Directory.CreateDirectory(_service.FileStore.RootFolder);
            Process.Start(new ProcessStartInfo(_service.FileStore.RootFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Could not open the snippets folder.", ex);
            SetStatus("Could not open the folder: " + ex.Message);
        }
    }

    private void RebuildIndex()
    {
        var answer = MessageBox.Show(this,
            $"Re-scan all .txt files in:\n{_service.FileStore.RootFolder}\n\nand rebuild the search index? " +
            "Snippets whose files are outside this folder will be removed from the list.",
            Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK)
        {
            return;
        }

        try
        {
            UseWaitCursor = true;
            var count = _service.RebuildIndex();
            ReloadCategories();
            ReloadList();
            SetStatus($"Index rebuilt: {count} snippets.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.Error("Rebuild index failed.", ex);
            MessageBox.Show(this, $"Rebuild failed:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var updated = dialog.Result;
        var folderChanged = !string.Equals(
            Path.GetFullPath(updated.SnippetsRootFolder), _service.FileStore.RootFolder, StringComparison.OrdinalIgnoreCase);

        _settings = updated;
        _service.DefaultCategory = updated.DefaultCategory;
        _service.SetDetectionThreshold(updated.DetectionThreshold);
        if (folderChanged)
        {
            _service.ChangeSnippetsFolder(updated.SnippetsRootFolder);
        }

        SetAutoCapture(updated.AutoCapture, save: false);
        SaveSettings();
        SetStatus("Settings saved.");

        if (folderChanged &&
            MessageBox.Show(this,
                "New snippets will be saved to the new folder.\n\nDo you also want to rebuild the index from that folder now?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            RebuildIndex();
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Could not save settings.", ex);
            SetStatus("Could not save settings: " + ex.Message);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---------------------------------------------------------------- list & preview

    private Snippet? SelectedSnippet => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Snippet : null;

    private void ReloadCategories()
    {
        var current = _categoryFilter.SelectedItem as string;
        _categoryFilter.BeginUpdate();
        _categoryFilter.Items.Clear();
        _categoryFilter.Items.Add(AllCategories);
        foreach (var category in _service.GetCategories())
        {
            _categoryFilter.Items.Add(category);
        }

        var index = current is null ? 0 : _categoryFilter.Items.IndexOf(current);
        _categoryFilter.SelectedIndex = index < 0 ? 0 : index;
        _categoryFilter.EndUpdate();
    }

    private void ReloadList(long? selectId = null)
    {
        selectId ??= SelectedSnippet?.Id;
        var category = _categoryFilter.SelectedItem as string;

        IReadOnlyList<Snippet> snippets;
        try
        {
            snippets = _service.Search(_searchBox.Text, category == AllCategories ? null : category);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _logger.Error("Search failed.", ex);
            SetStatus("Search failed: " + ex.Message);
            return;
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var snippet in snippets)
        {
            var item = new ListViewItem(snippet.Name) { Tag = snippet, ToolTipText = snippet.Tags };
            item.SubItems.Add(snippet.Category);
            item.SubItems.Add(snippet.ActionCount.ToString(CultureInfo.CurrentCulture));
            item.SubItems.Add(snippet.LastCopiedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            _list.Items.Add(item);

            if (snippet.Id == selectId)
            {
                item.Selected = true;
                item.Focused = true;
            }
        }

        _list.EndUpdate();
        _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
        _countLabel.Text = $"{snippets.Count} snippet(s)";
        ShowPreview();
    }

    private void ShowPreview()
    {
        _preview.Text = SelectedSnippet is { } snippet ? _service.ReadContent(snippet) : string.Empty;
    }

    private void SetStatus(string message) => _statusLabel.Text = message;
}

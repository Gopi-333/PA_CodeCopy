using System.Diagnostics;
using System.Runtime.InteropServices;
using PaCodeCopy.Core.Extraction;
using PaCodeCopy.Interop;

namespace PaCodeCopy.Forms;

/// <summary>"Extract PAD flow": copies every subflow of the flow open in the PAD designer with one click.</summary>
internal sealed partial class MainForm
{
    private const int WM_HOTKEY = 0x0312;
    private const int CancelHotkeyId = 0x5043; // arbitrary id for the Esc hotkey
    private const uint VK_ESCAPE = 0x1B;
    private static readonly TimeSpan ClipboardQuietPeriod = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _extractionCts;
    private ExtractionToast? _extractionToast;
    private DateTime _ignoreClipboardUntilUtc = DateTime.MinValue;

    /// <summary>True while extracting, and shortly after, so PAD's copies are not auto-captured as well.</summary>
    private bool IsExtractionBusy => _extractionCts is not null || DateTime.UtcNow < _ignoreClipboardUntilUtc;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == CancelHotkeyId && _extractionCts is { } cts)
        {
            cts.Cancel();
            _extractionToast?.SetText("Cancelling…");
            SetStatus("Cancelling the extraction…");
            return;
        }

        base.WndProc(ref m);
    }

    private async void ExtractPadFlow()
    {
        if (_extractionCts is not null)
        {
            SetStatus("An extraction is already running. Press Esc to cancel it.");
            return;
        }

        var window = await PickPadWindowAsync("Extract PAD flow");
        if (window is null)
        {
            return;
        }

        StartExtraction(window);
    }

    /// <summary>Finds the PAD designer window (asks when several are open). Null when none or cancelled.</summary>
    private async Task<PadWindow?> PickPadWindowAsync(string purpose, bool allowNone = false)
    {
        IReadOnlyList<PadWindow> windows;
        try
        {
            UseWaitCursor = true;
            // UI Automation calls must not run on this UI thread (it can deadlock on our own window).
            windows = await Task.Run(PadWindowFinder.FindDesignerWindows);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeInitializationException)
        {
            _logger.Error("UI Automation is not available.", ex);
            MessageBox.Show(this, $"Windows UI Automation is not available on this PC:\n{ex.Message}", purpose,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        finally
        {
            UseWaitCursor = false;
        }

        if (windows.Count == 0)
        {
            if (!allowNone)
            {
                MessageBox.Show(this,
                    "No Power Automate Desktop designer window was found.\n\n" +
                    "Open the flow you want in Power Automate Desktop (select the flow → Edit), " +
                    "then click 'Extract PAD flow' again.",
                    purpose, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return null;
        }

        if (windows.Count == 1)
        {
            return windows[0];
        }

        using var chooser = new ChoosePadWindowForm(windows, purpose);
        return chooser.ShowDialog(this) == DialogResult.OK ? chooser.Selected : null;
    }

    private void StartExtraction(PadWindow window)
    {
        var cts = new CancellationTokenSource();
        _extractionCts = cts;
        if (!RegisterHotKey(Handle, CancelHotkeyId, 0, VK_ESCAPE))
        {
            _logger.Warn("Could not register Esc as cancel key (another app uses it).");
        }

        _extractionToast = new ExtractionToast();
        _extractionToast.Show();
        var toast = _extractionToast;
        var progress = new Progress<string>(message =>
        {
            toast.SetText(message + "  Esc = cancel");
            SetStatus(message);
        });

        var extractor = new FlowExtractor(_service, _logger);
        _logger.Info($"Extraction started for PAD window '{window.Title}'.");

        // Clipboard access needs an STA thread; UI Automation must not run on the UI thread.
        var worker = new Thread(() =>
        {
            ExtractionReport? report = null;
            Exception? error = null;
            var originalClipboard = ClipboardHelper.TryGetText(_logger);
            try
            {
                report = extractor.Extract(new UiaPadDesigner(window, _logger), progress, cts.Token);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // Give the user their clipboard back.
                if (originalClipboard is not null)
                {
                    ClipboardHelper.TrySetText(originalClipboard, _logger);
                }
            }

            BeginInvoke((MethodInvoker)(() => FinishExtraction(report, error)));
        })
        {
            IsBackground = true,
            Name = "PadFlowExtraction",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void FinishExtraction(ExtractionReport? report, Exception? error)
    {
        UnregisterHotKey(Handle, CancelHotkeyId);
        _extractionToast?.Close();
        _extractionToast?.Dispose();
        _extractionToast = null;
        _extractionCts?.Dispose();
        _extractionCts = null;
        _ignoreClipboardUntilUtc = DateTime.UtcNow + ClipboardQuietPeriod; // the clipboard restore is still arriving

        RestoreFromTray();
        ReloadCategories();

        if (error is not null || report is null)
        {
            _logger.Error("Flow extraction failed.", error);
            SetStatus("Extraction failed: " + error?.Message);
            MessageBox.Show(this,
                $"The flow could not be extracted:\n{error?.Message}\n\n" +
                "Tip: use 'Scan PAD window…' and send the file it creates, so the extractor can be adjusted.",
                "Extract PAD flow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ReloadList();
            return;
        }

        SelectCategory(report.FlowName);
        SetStatus(report.Summary);

        var hint = report.CurrentSubflowOnly || report.Count(SubflowOutcome.Failed) > 0 || report.Count(SubflowOutcome.Empty) > 0
            ? "\n\nIf a subflow is missing or empty, use 'Scan PAD window…' and send the file it creates."
            : string.Empty;
        MessageBox.Show(this, report.Summary + "\n\n" + report.Details + hint, "Extract PAD flow",
            MessageBoxButtons.OK, report.Count(SubflowOutcome.Failed) > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>Shows only the extracted flow's snippets in the list.</summary>
    private void SelectCategory(string category)
    {
        var index = _categoryFilter.Items.IndexOf(category);
        if (index < 0)
        {
            // The file store may have cleaned the name; fall back to all snippets.
            ReloadList();
            return;
        }

        if (_categoryFilter.SelectedIndex == index)
        {
            ReloadList();
        }
        else
        {
            _categoryFilter.SelectedIndex = index; // reloads the list
        }
    }

    private async void ScanPadWindow()
    {
        var window = await PickPadWindowAsync("Scan PAD window", allowNone: true);
        try
        {
            UseWaitCursor = true;
            var path = await Task.Run(() => PadWindowFinder.ScanToFile(window));
            SetStatus("Scan saved: " + path);
            Process.Start("explorer.exe", $"/select,\"{path}\"");
            MessageBox.Show(this,
                $"The PAD window scan was saved to:\n{path}\n\n" +
                (window is null ? "No PAD designer window was open, so only the window list was saved.\n\n" : string.Empty) +
                "Check the file first (it can contain text from your flow), then send it if 'Extract PAD flow' " +
                "did not work, so the extractor can be adjusted.",
                "Scan PAD window", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            _logger.Error("PAD window scan failed.", ex);
            MessageBox.Show(this, $"The scan failed:\n{ex.Message}", "Scan PAD window", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

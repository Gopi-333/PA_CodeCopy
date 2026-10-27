using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using PaCodeCopy.Core.Extraction;
using PaCodeCopy.Core.Logging;

namespace PaCodeCopy.Interop;

/// <summary>
/// Finds out which PAD flow (and subflow) a copy came from, so auto-capture can save it in that flow's folder.
/// </summary>
/// <remarks>
/// The source is the foreground window (the user just pressed Ctrl+C in PAD) or, failing that, the process that
/// owns the clipboard (covers "Save from clipboard" clicked in this app). Only PAD.Designer windows count.
/// Call it off the UI thread: the subflow lookup uses UI Automation.
/// </remarks>
internal static class PadCopyContext
{
    private const string DesignerProcessPrefix = "PAD.Designer";
    private static readonly TimeSpan SubflowTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>The PAD flow the current clipboard content came from, or null when it is not from the PAD designer.</summary>
    public static PadCopySource? Detect(IAppLogger logger)
    {
        var window = FindSourceWindow();
        if (window is null)
        {
            return null;
        }

        var (handle, title) = window.Value;
        var subflow = TryGetSubflow(handle, logger);
        return new PadCopySource(PadWindowTitle.ParseFlowName(title), subflow);
    }

    /// <summary>Handle and title of the PAD designer window behind the copy, or null.</summary>
    public static (IntPtr Handle, string Title)? FindSourceWindow()
    {
        var foreground = GetForegroundWindow();
        if (IsDesignerProcess(ProcessIdOf(foreground)))
        {
            var title = WindowTitle(foreground);
            if (title.Length > 0)
            {
                return (foreground, title);
            }
        }

        // WPF apps (like PAD) own the clipboard through a hidden window, so look up the process's main window.
        var ownerProcess = ProcessIdOf(GetClipboardOwner());
        return IsDesignerProcess(ownerProcess) ? MainWindowOf(ownerProcess) : null;
    }

    private static string? TryGetSubflow(IntPtr handle, IAppLogger logger)
    {
        var lookup = Task.Run(() =>
        {
            try
            {
                return PadUia.FindSelectedSubflow(AutomationElement.FromHandle(handle));
            }
            catch (Exception ex) when (PadUia.IsAutomationFailure(ex))
            {
                logger.Warn("Could not read the open PAD subflow; saving without it.", ex);
                return null;
            }
        });

        // A slow or hung UI Automation call must not delay saving.
        return lookup.Wait(SubflowTimeout) ? lookup.Result : null;
    }

    private static bool IsDesignerProcess(uint processId)
    {
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.StartsWith(DesignerProcessPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static (IntPtr Handle, string Title)? MainWindowOf(uint processId)
    {
        (IntPtr, string)? found = null;
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero && ProcessIdOf(hwnd) == processId)
            {
                var title = WindowTitle(hwnd);
                if (title.Length > 0)
                {
                    found = (hwnd, title);
                    return false; // stop
                }
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static uint ProcessIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
}

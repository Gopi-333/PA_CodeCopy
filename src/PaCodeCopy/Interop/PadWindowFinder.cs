using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Automation;
using PaCodeCopy.Core.Configuration;
using PaCodeCopy.Core.Extraction;

namespace PaCodeCopy.Interop;

/// <summary>An open Power Automate Desktop designer window.</summary>
internal sealed record PadWindow(AutomationElement Element, IntPtr Handle, string Title, string ProcessName)
{
    public string FlowName => PadWindowTitle.ParseFlowName(Title);

    public override string ToString() => $"{FlowName}   ({Title})";
}

/// <summary>Finds PAD designer windows and writes UI-tree reports for troubleshooting.</summary>
internal static class PadWindowFinder
{
    // The flow designer runs as PAD.Designer.exe; the console (flow list) is PAD.Console.Host.exe.
    private const string DesignerProcessPrefix = "PAD.Designer";
    private const string ConsoleProcessPrefix = "PAD.Console";
    private const int MaxScanDepth = 14;
    private const int MaxScanNodes = 6000;

    public static IReadOnlyList<PadWindow> FindDesignerWindows()
    {
        var byProcess = new List<PadWindow>();
        var byTitle = new List<PadWindow>();
        var ownProcessId = Environment.ProcessId;

        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                var info = window.Current;
                if (info.ProcessId == ownProcessId || string.IsNullOrWhiteSpace(info.Name))
                {
                    continue;
                }

                var processName = ProcessName(info.ProcessId);
                var padWindow = new PadWindow(window, new IntPtr(info.NativeWindowHandle), info.Name, processName);
                if (processName.StartsWith(DesignerProcessPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    byProcess.Add(padWindow);
                }
                else if (PadWindowTitle.LooksLikePad(info.Name) &&
                         !processName.StartsWith(ConsoleProcessPrefix, StringComparison.OrdinalIgnoreCase) &&
                         info.Name.Contains(" | ", StringComparison.Ordinal))
                {
                    byTitle.Add(padWindow);
                }
            }
            catch (ElementNotAvailableException)
            {
                // Window closed while we were looking at it.
            }
        }

        return byProcess.Count > 0 ? byProcess : byTitle;
    }

    /// <summary>
    /// Writes the UI Automation tree of a PAD window (or, without one, the list of top-level windows) to
    /// Documents\PaCodeCopy\Diagnostics and returns the file path.
    /// </summary>
    public static string ScanToFile(PadWindow? window)
    {
        Directory.CreateDirectory(AppPaths.DiagnosticsFolder);
        var path = Path.Combine(AppPaths.DiagnosticsFolder, $"pad-ui-{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var text = new StringBuilder()
            .AppendLine("PA CodeCopy - PAD window scan")
            .AppendLine($"Created: {DateTime.Now.ToString("u", CultureInfo.InvariantCulture)}")
            .AppendLine($"Windows: {Environment.OSVersion}")
            .AppendLine("Review this file before sharing it: element names can contain text from your flow.")
            .AppendLine();

        text.AppendLine("Top-level windows (process | title):");
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                text.AppendLine($"  {ProcessName(top.Current.ProcessId)} | {top.Current.Name}");
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        text.AppendLine();
        if (window is null)
        {
            text.AppendLine("No PAD designer window was found. Open a flow in Power Automate Desktop and scan again.");
        }
        else
        {
            text.AppendLine($"PAD window: '{window.Title}' (process {window.ProcessName})").AppendLine();
            var count = 0;
            WriteTree(text, window.Element, 0, ref count);
            if (count >= MaxScanNodes)
            {
                text.AppendLine($"... stopped after {MaxScanNodes} elements.");
            }
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static void WriteTree(StringBuilder text, AutomationElement element, int depth, ref int count)
    {
        if (depth > MaxScanDepth || count >= MaxScanNodes)
        {
            return;
        }

        count++;
        try
        {
            var info = element.Current;
            var rect = info.BoundingRectangle;
            var bounds = rect.IsEmpty ? "-" : $"{rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}";
            text.Append(' ', depth * 2)
                .Append(info.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal))
                .Append(" name=\"").Append(Shorten(info.Name)).Append('"')
                .Append(" id=\"").Append(info.AutomationId).Append('"')
                .Append(" class=\"").Append(info.ClassName).Append('"')
                .Append(" bounds=").Append(bounds)
                .Append(info.IsOffscreen ? " offscreen" : string.Empty)
                .AppendLine();

            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            while (child is not null && count < MaxScanNodes)
            {
                WriteTree(text, child, depth + 1, ref count);
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException)
        {
            text.Append(' ', depth * 2).AppendLine("(element disappeared)");
        }
    }

    private static string Shorten(string value)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length > 80 ? oneLine[..80] + "…" : oneLine;
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }
}

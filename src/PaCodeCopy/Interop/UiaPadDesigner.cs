using System.Diagnostics;
using System.Windows.Automation;
using Condition = System.Windows.Automation.Condition;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using PaCodeCopy.Core.Extraction;
using PaCodeCopy.Core.Logging;

namespace PaCodeCopy.Interop;

/// <summary>
/// Drives an open PAD designer window through UI Automation: selects each subflow tab, then sends
/// Ctrl+A / Ctrl+C to copy its actions. Only selection and copy keystrokes are sent, so the flow is never changed.
/// </summary>
/// <remarks>
/// PAD's UI-automation names are not documented, so the tabs and the action list are found with generic rules:
/// the subflow tab strip is the tab control that contains a tab named "Main", and the action list is the
/// largest list/grid/tree under that tab strip. "Scan PAD window…" writes the real UI tree when these rules miss.
/// Must be called on an STA thread (clipboard access).
/// </remarks>
internal sealed class UiaPadDesigner : IPadDesigner
{
    private const string MainSubflowName = "Main";
    private static readonly TimeSpan ForegroundTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TabSwitchTimeout = TimeSpan.FromSeconds(2);
    private const int RenderDelayMs = 600;

    private static readonly ControlType[] ActionListTypes =
        { ControlType.List, ControlType.DataGrid, ControlType.Tree, ControlType.Table };

    private readonly PadWindow _window;
    private readonly AutomationElement _root;
    private readonly IAppLogger _logger;
    private List<AutomationElement> _tabs;
    private List<string> _tabNames;
    private readonly HashSet<int> _copiedTabs = new();

    public UiaPadDesigner(PadWindow window, IAppLogger logger)
    {
        _window = window;
        // Look the window up again on this (worker) thread instead of reusing an element from another thread.
        _root = AutomationElement.FromHandle(window.Handle);
        _logger = logger;
        FlowName = window.FlowName;
        _tabs = FindSubflowTabs();
        _tabNames = _tabs.Select(SafeName).ToList();
        _logger.Info($"PAD window '{window.Title}': found {_tabs.Count} subflow tab(s): {string.Join(", ", _tabs.Select(Describe))}.");
    }

    public string FlowName { get; }

    /// <summary>Tab names in screen order. Kept even if two tabs report the same name; tabs are used by position.</summary>
    public IReadOnlyList<string> GetSubflowNames() => _tabNames;

    public string? CopySubflow(string? subflowName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeInput.BringToFront(_window.Handle, ForegroundTimeout))
        {
            throw new InvalidOperationException("Could not bring the PAD designer to the front.");
        }

        AutomationElement? tab = null;
        if (subflowName is not null)
        {
            tab = SelectTab(subflowName);
            Thread.Sleep(RenderDelayMs); // let PAD draw the subflow's actions
        }

        cancellationToken.ThrowIfCancellationRequested();
        FocusActionList(tab);

        // Never type into another window: the user may have clicked somewhere else meanwhile.
        if (!NativeInput.IsForeground(_window.Handle) && !NativeInput.BringToFront(_window.Handle, ForegroundTimeout))
        {
            throw new InvalidOperationException("The PAD designer is no longer the active window.");
        }

        var before = NativeInput.ClipboardSequenceNumber();
        NativeInput.SendCtrl('A');
        Thread.Sleep(250);
        NativeInput.SendCtrl('C');

        var stopwatch = Stopwatch.StartNew();
        while (NativeInput.ClipboardSequenceNumber() == before)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > CopyTimeout)
            {
                return null; // nothing selected → nothing copied → empty subflow
            }

            Thread.Sleep(50);
        }

        Thread.Sleep(100); // PAD may set several clipboard formats
        return ClipboardHelper.TryGetText(_logger);
    }

    private List<AutomationElement> FindSubflowTabs()
    {
        try
        {
            var mainTab = _root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.NameProperty, MainSubflowName, PropertyConditionFlags.IgnoreCase)));
            if (mainTab is null)
            {
                return new List<AutomationElement>();
            }

            var strip = TreeWalker.ControlViewWalker.GetParent(mainTab);
            if (strip is null)
            {
                return new List<AutomationElement> { mainTab };
            }

            return strip.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>()
                .ToList();
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            // Without tabs the extractor still copies the subflow that is open.
            _logger.Warn("Could not read PAD's subflow tabs; only the open subflow will be copied.", ex);
            return new List<AutomationElement>();
        }
    }

    private AutomationElement SelectTab(string name)
    {
        // Use the next not-yet-copied tab with this name (by position), so duplicate names still work.
        var index = Enumerable.Range(0, _tabNames.Count)
            .FirstOrDefault(i => _tabNames[i] == name && !_copiedTabs.Contains(i), -1);
        if (index < 0)
        {
            throw new InvalidOperationException($"Subflow tab '{name}' was not found.");
        }

        _copiedTabs.Add(index);
        var tab = _tabs[index];
        if (!IsAlive(tab))
        {
            _tabs = FindSubflowTabs(); // tabs may have been re-created
            tab = index < _tabs.Count ? _tabs[index] : throw new InvalidOperationException($"Subflow tab '{name}' is gone.");
        }

        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
        }
        else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
        }
        else
        {
            ClickCenter(tab.Current.BoundingRectangle);
        }

        var stopwatch = Stopwatch.StartNew();
        bool? selected = null;
        while (stopwatch.Elapsed < TabSwitchTimeout)
        {
            if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var state))
            {
                break;
            }

            selected = ((SelectionItemPattern)state).Current.IsSelected;
            if (selected == true)
            {
                break;
            }

            Thread.Sleep(100);
        }

        _logger.Info($"Subflow tab '{name}': selected={selected?.ToString() ?? "unknown"}.");
        return tab;
    }

    private static bool IsAlive(AutomationElement element)
    {
        try
        {
            _ = element.Current.BoundingRectangle;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>Puts keyboard focus on the action list, so Ctrl+A selects the actions.</summary>
    private void FocusActionList(AutomationElement? tab)
    {
        try
        {
            FocusActionListCore(tab);
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            // Keep PAD's current focus; in the designer that is usually the action list already.
            _logger.Warn("Could not locate PAD's action list; using the current focus.", ex);
        }
    }

    private void FocusActionListCore(AutomationElement? tab)
    {
        // A point just below the subflow tabs lies inside the action list.
        var anchor = tab ?? _tabs.FirstOrDefault();
        Point? probe = null;
        if (anchor is not null)
        {
            var tabRect = anchor.Current.BoundingRectangle;
            var stripRect = TreeWalker.ControlViewWalker.GetParent(anchor)?.Current.BoundingRectangle ?? tabRect;
            probe = new Point(stripRect.Left + Math.Min(stripRect.Width, 600) / 2, tabRect.Bottom + 60);
        }

        var candidates = _root.FindAll(TreeScope.Descendants, new OrCondition(
                ActionListTypes.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray()))
            .Cast<AutomationElement>()
            .Where(e => !e.Current.IsOffscreen && !e.Current.BoundingRectangle.IsEmpty)
            .ToList();

        var list = candidates
            .Where(e => probe is null || e.Current.BoundingRectangle.Contains(probe.Value))
            .OrderByDescending(e => Area(e.Current.BoundingRectangle))
            .FirstOrDefault()
            ?? candidates.OrderByDescending(e => Area(e.Current.BoundingRectangle)).FirstOrDefault();

        if (list is not null)
        {
            _logger.Info($"Action list: {Describe(list)} (probe {probe?.ToString() ?? "none"}, {candidates.Count} candidates).");
            try
            {
                list.SetFocus();
                return;
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn("Could not focus the PAD action list; clicking it instead.", ex);
            }

            ClickCenter(list.Current.BoundingRectangle);
            return;
        }

        _logger.Info($"No action list found; clicking below the tabs at {probe?.ToString() ?? "(none)"}.");
        if (probe is not null)
        {
            // No list found: click just below the tabs (a single click only selects; it never edits).
            NativeInput.Click((int)probe.Value.X, (int)probe.Value.Y);
        }
    }

    private static void ClickCenter(Rect rect)
    {
        if (!rect.IsEmpty)
        {
            NativeInput.Click((int)(rect.Left + rect.Width / 2), (int)(rect.Top + Math.Min(rect.Height / 2, 40)));
        }
    }

    private static bool IsAutomationFailure(Exception ex) =>
        ex is ElementNotAvailableException or NotImplementedException or NotSupportedException
            or InvalidOperationException or System.Runtime.InteropServices.COMException;

    private static string Describe(AutomationElement element)
    {
        var info = element.Current;
        return $"{info.ControlType.ProgrammaticName} name='{info.Name}' id='{info.AutomationId}' bounds={info.BoundingRectangle}";
    }

    private static double Area(Rect rect) => rect.IsEmpty ? 0 : rect.Width * rect.Height;

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Current.Name?.Trim() ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }
}

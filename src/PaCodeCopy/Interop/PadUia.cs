using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace PaCodeCopy.Interop;

/// <summary>UI Automation rules for the PAD designer, shared by "Extract PAD flow" and auto-capture.</summary>
internal static class PadUia
{
    private const string MainSubflowName = "Main";

    /// <summary>
    /// The subflow tabs in screen order: all tab items in the tab strip that contains a tab named "Main".
    /// Empty when there is no such tab. UI Automation exceptions are passed to the caller.
    /// </summary>
    public static List<AutomationElement> FindSubflowTabs(AutomationElement window)
    {
        var mainTab = window.FindFirst(TreeScope.Descendants, new AndCondition(
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

    /// <summary>Name of the subflow tab that is selected, or null when it cannot be read.</summary>
    public static string? FindSelectedSubflow(AutomationElement window)
    {
        foreach (var tab in FindSubflowTabs(window))
        {
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
                ((SelectionItemPattern)pattern).Current.IsSelected)
            {
                var name = tab.Current.Name?.Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
        }

        return null;
    }

    /// <summary>Exceptions that mean "UI Automation could not read this window" rather than a bug.</summary>
    public static bool IsAutomationFailure(Exception ex) =>
        ex is ElementNotAvailableException or NotImplementedException or NotSupportedException
            or InvalidOperationException or COMException or ArgumentException;
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FakePadDesigner;

/// <summary>
/// Test double for the Power Automate Desktop designer (a WPF app, like the real PAD.Designer.exe):
/// subflow tabs whose action lists support Ctrl+A (select all) and Ctrl+C (copy as PAD script).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var tabs = new TabControl();
        AddTab(tabs, "Main", "SET Total TO 0", "CALL Login", "LOOP FOREACH Row IN Invoices",
            "    Variables.IncreaseVariable Value: Total IncrementValue: Row[2]", "END");
        AddTab(tabs, "Login", "WebAutomation.LaunchChrome.LaunchChrome Url: $'''https://example.com''' BrowserInstance=> Browser",
            "WAIT 2");
        AddTab(tabs, "Empty");

        var window = new Window
        {
            Title = "Demo invoices | Power Automate",
            Width = 900,
            Height = 600,
            Left = 40,
            Top = 40,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = tabs,
        };
        new Application().Run(window);
    }

    private static void AddTab(TabControl tabs, string name, params string[] lines)
    {
        var list = new ListBox { SelectionMode = SelectionMode.Extended };
        foreach (var line in lines)
        {
            list.Items.Add(line);
        }

        // Ctrl+A is built into ListBox; Ctrl+C copies the selected actions in list order, like PAD.
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) =>
        {
            var selected = list.Items.Cast<string>().Where(item => list.SelectedItems.Contains(item)).ToList();
            if (selected.Count > 0)
            {
                Clipboard.SetText(string.Join("\r\n", selected) + "\r\n");
            }
        }));

        tabs.Items.Add(new TabItem { Header = name, Content = list });
    }
}

// Test double for the Power Automate Desktop designer: subflow tabs whose list copies PAD script with Ctrl+A / Ctrl+C.
static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "Demo invoices | Power Automate", Size = new Size(900, 600), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        AddTab(tabs, "Main", "SET Total TO 0", "CALL Login", "LOOP FOREACH Row IN Invoices", "    Variables.IncreaseVariable Value: Total IncrementValue: Row[2]", "END");
        AddTab(tabs, "Login", "WebAutomation.LaunchChrome.LaunchChrome Url: $'''https://example.com''' BrowserInstance=> Browser", "WAIT 2");
        AddTab(tabs, "Empty");
        form.Controls.Add(tabs);
        Application.Run(form);
    }

    static void AddTab(TabControl tabs, string name, params string[] lines)
    {
        var page = new TabPage(name);
        var list = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended };
        list.Items.AddRange(lines);
        list.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                for (var i = 0; i < list.Items.Count; i++) list.SetSelected(i, true);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C && list.SelectedItems.Count > 0)
            {
                Clipboard.SetText(string.Join("\r\n", list.SelectedItems.Cast<string>()) + "\r\n");
                e.Handled = true;
            }
        };
        page.Controls.Add(list);
        tabs.TabPages.Add(page);
    }
}

using PaCodeCopy.Interop;

namespace PaCodeCopy.Forms;

/// <summary>Lets the user pick a flow when several PAD designer windows are open.</summary>
internal sealed class ChoosePadWindowForm : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public ChoosePadWindowForm(IReadOnlyList<PadWindow> windows, string title)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        ClientSize = new Size(520, 260);

        _list.Items.AddRange(windows.Cast<object>().ToArray());
        _list.SelectedIndex = 0;
        _list.DoubleClick += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        var label = new Label { Text = "Several flows are open in Power Automate Desktop. Which one?", Dock = DockStyle.Top, AutoSize = false, Height = 28, Padding = new Padding(8, 8, 8, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        listPanel.Controls.Add(_list);

        Controls.Add(listPanel);
        Controls.Add(label);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public PadWindow? Selected => _list.SelectedItem as PadWindow;
}

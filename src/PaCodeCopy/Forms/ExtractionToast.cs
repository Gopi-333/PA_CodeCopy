namespace PaCodeCopy.Forms;

/// <summary>
/// Small always-on-top progress box in the bottom-right corner while a flow is extracted.
/// It never takes keyboard focus, so the keystrokes sent to PAD are not disturbed.
/// </summary>
internal sealed class ExtractionToast : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Label _label = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 12, 0),
        ForeColor = Color.White,
    };

    public ExtractionToast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 64, 112);
        Font = new Font(SystemFonts.MessageBoxFont ?? Font, FontStyle.Bold);
        Size = new Size(460, 64);
        Controls.Add(_label);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        SetText("Starting… Don't use the mouse or keyboard. Press Esc to cancel.");
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void SetText(string text) => _label.Text = "PA CodeCopy: " + text;
}

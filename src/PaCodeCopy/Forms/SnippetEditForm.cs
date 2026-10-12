namespace PaCodeCopy.Forms;

/// <summary>
/// Dialog for editing a snippet's name, category and tags. In "paste" mode it also shows a code box,
/// so the user can paste any code (not only auto-detected PAD code) and save it.
/// </summary>
internal sealed class SnippetEditForm : Form
{
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _categoryBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _tagsBox = new() { Dock = DockStyle.Fill, PlaceholderText = "comma separated, e.g. excel, invoices" };
    private readonly TextBox? _codeBox;

    public SnippetEditForm(string title, IEnumerable<string> categories, string name, string category, string tags, bool showCodeBox)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = showCodeBox ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = showCodeBox;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = showCodeBox ? new Size(720, 520) : new Size(460, 150);
        MinimumSize = showCodeBox ? new Size(500, 360) : Size.Empty;

        _nameBox.Text = name;
        _categoryBox.Items.AddRange(categories.Cast<object>().ToArray());
        _categoryBox.Text = category;
        _tagsBox.Text = tags;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, "&Name:", _nameBox);
        AddRow(layout, "&Category:", _categoryBox);
        AddRow(layout, "&Tags:", _tagsBox);

        if (showCodeBox)
        {
            _codeBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 10f),
                PlaceholderText = "Paste Power Automate Desktop code here (Ctrl+V)",
            };
            var codeLabel = new Label { Text = "C&ode:", AutoSize = true, Anchor = AnchorStyles.Left };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(codeLabel, 0, layout.RowCount);
            layout.SetColumnSpan(codeLabel, 2);
            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(_codeBox, 0, layout.RowCount);
            layout.SetColumnSpan(_codeBox, 2);
            layout.RowCount++;
        }

        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(10, 0, 10, 10),
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = showCodeBox ? null : okButton; // Enter must add new lines in the code box.
        CancelButton = cancelButton;

        okButton.Click += (_, _) => ValidateAndClose();
        Shown += (_, _) => (_codeBox ?? (Control)_nameBox).Focus();
    }

    public string SnippetName => _nameBox.Text.Trim();

    public string Category => _categoryBox.Text.Trim();

    public string Tags => _tagsBox.Text.Trim();

    public string Code => _codeBox?.Text ?? string.Empty;

    private void ValidateAndClose()
    {
        if (_codeBox is not null && string.IsNullOrWhiteSpace(_codeBox.Text))
        {
            MessageBox.Show(this, "Paste some code first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            _codeBox.Focus();
            return;
        }

        // In paste mode an empty name is allowed: a default name is generated.
        if (_codeBox is null && string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            MessageBox.Show(this, "Name is required.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            _nameBox.Focus();
        }
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, layout.RowCount);
        layout.Controls.Add(control, 1, layout.RowCount);
        layout.RowCount++;
    }
}

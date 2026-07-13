namespace WoWCrucible.App;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _core = new() { Dock = DockStyle.Fill };
    private readonly TextBox _client = new() { Dock = DockStyle.Fill };
    private readonly TextBox _schema = new() { Dock = DockStyle.Fill };
    private readonly TextBox _baseDbc = new() { Dock = DockStyle.Fill };
    private readonly TextBox _overrideDbc = new() { Dock = DockStyle.Fill };

    public SettingsForm(AppSettings settings)
    {
        Text = "WoW Crucible Paths"; Width = 850; Height = 340; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        _core.Text = settings.CoreDbcPath; _client.Text = settings.ClientDataPath; _schema.Text = settings.SchemaDefinitionPath;
        _baseDbc.Text = settings.BaseDbcPath; _overrideDbc.Text = settings.OverrideDbcPath;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), ColumnCount = 3, RowCount = 6 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 145)); table.ColumnStyles.Add(new(SizeType.Percent, 100)); table.ColumnStyles.Add(new(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "Server data\\dbc:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        table.Controls.Add(_core, 1, 0); table.Controls.Add(Browse(_core, "Select the server data\\dbc folder"), 2, 0);
        table.Controls.Add(new Label { Text = "WoW client Data:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        table.Controls.Add(_client, 1, 1); table.Controls.Add(Browse(_client, "Select the WoW client Data folder"), 2, 1);
        table.Controls.Add(new Label { Text = "WotLK schema XML:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        table.Controls.Add(_schema, 1, 2); table.Controls.Add(BrowseFile(_schema), 2, 2);
        table.Controls.Add(new Label { Text = "Base DBC layer:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        table.Controls.Add(_baseDbc, 1, 3); table.Controls.Add(Browse(_baseDbc, "Select the base DBC directory"), 2, 3);
        table.Controls.Add(new Label { Text = "Override DBC layer:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        table.Controls.Add(_overrideDbc, 1, 4); table.Controls.Add(Browse(_overrideDbc, "Select the Area52/override DBC directory"), 2, 4);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true });
        buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true });
        table.Controls.Add(buttons, 0, 5); table.SetColumnSpan(buttons, 3); Controls.Add(table);
        AcceptButton = buttons.Controls[0] as Button; CancelButton = buttons.Controls[1] as Button;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (!ValidFolder(_core.Text, "server DBC", e) || !ValidFolder(_client.Text, "client Data", e) || !ValidFolder(_baseDbc.Text, "base DBC", e) || !ValidFolder(_overrideDbc.Text, "override DBC", e)) return;
            if (!string.IsNullOrWhiteSpace(_schema.Text) && !File.Exists(_schema.Text)) { MessageBox.Show(this, $"The schema XML does not exist:\n{_schema.Text}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); e.Cancel = true; return; }
            settings.CoreDbcPath = _core.Text.Trim(); settings.ClientDataPath = _client.Text.Trim(); settings.SchemaDefinitionPath = _schema.Text.Trim();
            settings.BaseDbcPath = _baseDbc.Text.Trim(); settings.OverrideDbcPath = _overrideDbc.Text.Trim(); settings.Save();
        };
    }

    private Button BrowseFile(TextBox target)
    {
        var button = new Button { Text = "Browse…", AutoSize = true };
        button.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = "WDBX schema (*.xml)|*.xml|All files (*.*)|*.*", FileName = Path.GetFileName(target.Text), InitialDirectory = File.Exists(target.Text) ? Path.GetDirectoryName(target.Text) : string.Empty }; if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName; };
        return button;
    }

    private Button Browse(TextBox target, string description)
    {
        var button = new Button { Text = "Browse…", AutoSize = true };
        button.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = description, SelectedPath = Directory.Exists(target.Text) ? target.Text : string.Empty }; if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath; };
        return button;
    }

    private bool ValidFolder(string path, string label, FormClosingEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path)) return true;
        MessageBox.Show(this, $"The {label} folder does not exist:\n{path}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); e.Cancel = true; return false;
    }
}

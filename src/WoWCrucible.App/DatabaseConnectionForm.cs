using MySqlConnector;
using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class DatabaseConnectionForm : Form
{
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 3306, Dock = DockStyle.Fill };
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _database = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _ssl = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _inspect = new() { Text = "Test and inspect", AutoSize = true };
    private readonly Button _use = new() { Text = "Use connection", AutoSize = true, Enabled = false };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new(680, 0) };
    private readonly AppSettings _settings;

    public DatabaseConnectionProfile? Profile { get; private set; }
    public DatabaseCapabilities? Capabilities { get; private set; }

    public DatabaseConnectionForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Connect to a World Database"; Width = 760; Height = 440; StartPosition = FormStartPosition.CenterParent;
        _host.Text = settings.DatabaseHost; _port.Value = Math.Clamp(settings.DatabasePort, 1, 65535); _user.Text = settings.DatabaseUser; _database.Text = settings.WorldDatabase;
        _ssl.DataSource = Enum.GetValues<MySqlSslMode>();
        if (Enum.TryParse<MySqlSslMode>(settings.DatabaseSslMode, out var ssl)) _ssl.SelectedItem = ssl;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 2, RowCount = 8 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 150)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        Add(table, "Host", _host, 0); Add(table, "Port", _port, 1); Add(table, "User", _user, 2); Add(table, "Password", _password, 3); Add(table, "World database", _database, 4); Add(table, "TLS/SSL mode", _ssl, 5);
        var passwordNote = new Label { Text = "The password is kept only in memory and is never written to settings.", AutoSize = true, ForeColor = Color.DimGray, Margin = new(0, 8, 0, 8) };
        table.Controls.Add(passwordNote, 0, 6); table.SetColumnSpan(passwordNote, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        footer.Controls.Add(_inspect); footer.Controls.Add(_use); footer.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true }); footer.Controls.Add(_result);
        table.Controls.Add(footer, 0, 7); table.SetColumnSpan(footer, 2); Controls.Add(table);
        _inspect.Click += Inspect;
        _use.Click += (_, _) => { SaveNonSecretSettings(); DialogResult = DialogResult.OK; Close(); };
        _host.TextChanged += InvalidateInspection; _port.ValueChanged += InvalidateInspection; _user.TextChanged += InvalidateInspection;
        _password.TextChanged += InvalidateInspection; _database.TextChanged += InvalidateInspection; _ssl.SelectedValueChanged += InvalidateInspection;
        AcceptButton = _inspect; CancelButton = footer.Controls[2] as Button;
    }

    private void InvalidateInspection(object? sender, EventArgs e)
    {
        if (!_inspect.Enabled) return;
        Profile = null; Capabilities = null; _use.Enabled = false;
        if (!string.IsNullOrWhiteSpace(_result.Text)) _result.Text = "Connection details changed. Test and inspect again before using them.";
    }

    private async void Inspect(object? sender, EventArgs e)
    {
        try
        {
            Toggle(false); _result.Text = "Connecting and reading schema capabilities…";
            Profile = new(_host.Text.Trim(), (uint)_port.Value, _user.Text.Trim(), _password.Text, _database.Text.Trim(), (MySqlSslMode)_ssl.SelectedItem!);
            Capabilities = await new DatabaseCapabilityService().InspectAsync(Profile);
            var found = DatabaseCapabilityService.ExpectedTables.Where(name => Capabilities.FindTable(name) is not null).ToArray();
            _result.Text = $"Connected to MySQL {Capabilities.ServerVersion}. Found {found.Length} relevant tables: {(found.Length == 0 ? "none" : string.Join(", ", found))}.";
            _use.Enabled = found.Length > 0; SaveNonSecretSettings();
        }
        catch (Exception ex) { Profile = null; Capabilities = null; _result.Text = $"Connection failed: {ex.Message}"; }
        finally { _inspect.Enabled = true; _host.Enabled = _port.Enabled = _user.Enabled = _password.Enabled = _database.Enabled = _ssl.Enabled = true; }
    }

    private void Toggle(bool enabled) { _inspect.Enabled = enabled; _use.Enabled = false; _host.Enabled = _port.Enabled = _user.Enabled = _password.Enabled = _database.Enabled = _ssl.Enabled = enabled; }
    private void SaveNonSecretSettings() { _settings.DatabaseHost = _host.Text.Trim(); _settings.DatabasePort = (uint)_port.Value; _settings.DatabaseUser = _user.Text.Trim(); _settings.WorldDatabase = _database.Text.Trim(); _settings.DatabaseSslMode = _ssl.SelectedItem?.ToString() ?? "Preferred"; _settings.Save(); }
    private static void Add(TableLayoutPanel table, string label, Control control, int row) { table.Controls.Add(new Label { Text = label + ":", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); table.Controls.Add(control, 1, row); }
}

using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ServerWorkspaceForm : Form
{
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly Button _detect = new() { Text = "Detect Server", AutoSize = true };
    private readonly Button _apply = new() { Text = "Use Workspace and Connect", AutoSize = true, Enabled = false };
    private readonly TextBox _summary = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    public ServerWorkspace? Workspace { get; private set; }

    public ServerWorkspaceForm(AppSettings settings)
    {
        Text = "Detect Server Workspace"; Width = 830; Height = 500; StartPosition = FormStartPosition.CenterParent;
        _folder.Text = Directory.Exists(settings.ServerRootPath) ? settings.ServerRootPath : Directory.Exists(settings.CoreDbcPath) ? Directory.GetParent(Directory.GetParent(settings.CoreDbcPath)?.FullName ?? string.Empty)?.FullName ?? string.Empty : string.Empty;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(14), ColumnCount = 3, RowCount = 4 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 105)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Server folder:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); layout.Controls.Add(_folder, 1, 0);
        var browse = new Button { Text = "Browse…", AutoSize = true }; layout.Controls.Add(browse, 2, 0);
        var explanation = new Label { Text = "Select an installed server folder—not a source checkout. Crucible finds the live worldserver.conf, server DBCs, and world database. WSL wrapper folders are supported.", AutoSize = true, MaximumSize = new(760, 0), ForeColor = Color.DimGray, Margin = new(0, 10, 0, 10) };
        layout.Controls.Add(explanation, 0, 1); layout.SetColumnSpan(explanation, 3); layout.Controls.Add(_summary, 0, 2); layout.SetColumnSpan(_summary, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(_detect); buttons.Controls.Add(_apply); buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true }); layout.Controls.Add(buttons, 0, 3); layout.SetColumnSpan(buttons, 3);
        Controls.Add(layout); AcceptButton = _detect; CancelButton = buttons.Controls[2] as Button;
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Select the installed AzerothCore or TrinityCore server folder", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : string.Empty }; if (dialog.ShowDialog(this) == DialogResult.OK) { _folder.Text = dialog.SelectedPath; Workspace = null; _apply.Enabled = false; } };
        _detect.Click += async (_, _) => await DetectAsync();
        _apply.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
    }

    private async Task DetectAsync()
    {
        try
        {
            Toggle(false); _summary.Text = "Inspecting server folder…"; Workspace = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Workspace = await ServerWorkspaceDetector.DetectAsync(_folder.Text, timeout.Token);
            var dbc = string.IsNullOrWhiteSpace(Workspace.DbcPath) ? "Not found — can be set manually" : $"{Workspace.DbcPath} ({Directory.EnumerateFiles(Workspace.DbcPath, "*.dbc").Count():N0} files)";
            _summary.Text = $"Detected successfully\r\n\r\nCore family: {Workspace.CoreFamily}\r\nConfiguration: {Workspace.ConfigLocation}\r\nServer DBCs: {dbc}\r\nWorld database: {Workspace.WorldDatabase.Database} on {Workspace.WorldDatabase.Host}:{Workspace.WorldDatabase.Port}\r\nDatabase user: {Workspace.WorldDatabase.User}\r\nLayout: {(Workspace.UsesWsl ? "Windows workspace + WSL server" : "Native/local server folder")}\r\n\r\nThe database password was detected but will remain in memory only. Applying this workspace will inspect the live database; it will not modify content by itself.";
            _apply.Enabled = true;
        }
        catch (Exception ex) { _summary.Text = $"Could not detect this server workspace:\r\n\r\n{ex.Message}\r\n\r\nYou can still configure paths and database details manually."; }
        finally { _detect.Enabled = _folder.Enabled = true; }
    }

    private void Toggle(bool enabled) { _detect.Enabled = _folder.Enabled = enabled; _apply.Enabled = false; }

}

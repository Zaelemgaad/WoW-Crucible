using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class StartCenterControl : UserControl
{
    private readonly AppSettings _settings;
    private readonly TableLayoutPanel _readiness = new() { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new(0, 4, 0, 8) };

    private readonly Func<string?> _databaseStatus;

    public StartCenterControl(AppSettings settings, Action editSpells, Action createItem, Action detectServer, Action connectDatabase, Func<string?> databaseStatus, Action openDbcs, Action buildPatch, Action browseMpq, Action compareLayers, Action configurePaths)
    {
        _settings = settings; _databaseStatus = databaseStatus; Dock = DockStyle.Fill; AutoScroll = true; BackColor = Color.FromArgb(244, 246, 249);
        var content = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top, Padding = new(36, 28, 36, 36), BackColor = BackColor };
        content.Controls.Add(new Label { Text = "WoW Crucible", Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(31, 41, 55), Margin = new(0, 0, 0, 3) });
        content.Controls.Add(new Label { Text = "Choose what you want to accomplish. Guided workflows explain the WoW concepts; every raw field remains available in the advanced editor.", Font = new Font("Segoe UI", 11), AutoSize = true, MaximumSize = new(1050, 0), ForeColor = Color.FromArgb(75, 85, 99), Margin = new(0, 0, 0, 22) });
        content.Controls.Add(Heading("Start here"));
        var guided = CardRow();
        guided.Controls.Add(Card("Create or edit a spell", "Open Spell.dbc directly into the grouped Spell Workspace. Names, effects, costs, flags, visuals, and linked records stay readable.", "Open Spell Workspace", editSpells), 0, 0);
        guided.Controls.Add(Card("Create an item", "Use named item, weapon, armor, binding, and stat fields. Preview or export SQL, then optionally insert through a detected live schema.", "Open Item Creator", createItem), 1, 0);
        guided.Controls.Add(Card("Build a client patch", "Stage edited DBC or UI files, review every path inside the MPQ, enforce a manifest policy, then create a small patch archive.", "Build Patch MPQ", buildPatch), 2, 0);
        content.Controls.Add(guided);
        content.Controls.Add(Heading("Advanced tools"));
        var advanced = CardRow();
        advanced.Controls.Add(Card("Open any DBC", "Use the fast virtual grid, decoded values, batch cloning, search, raw mode, undo, and multi-file staging.", "Open DBC Files", openDbcs), 0, 0);
        advanced.Controls.Add(Card("Inspect an existing MPQ", "Search a patch without loading its contents, inspect internal paths, and extract selected files or the whole archive.", "Browse or Extract", browseMpq), 1, 0);
        advanced.Controls.Add(Card("Compare DBC layers", "Compare base and override directories, inspect semantic changes, then promote selected fields or rows by ID.", "Open Layer Comparison", compareLayers), 2, 0);
        content.Controls.Add(advanced);
        var workspace = CardRow();
        workspace.Controls.Add(Card("Detect your server automatically", "Choose the installed server folder. Crucible finds its live config, DBC directory, and world database—even for a Windows/WSL split layout.", "Detect Server Workspace", () => { detectServer(); RefreshReadiness(); }), 0, 0);
        workspace.Controls.Add(Card("Connect manually", "Use explicit host, port, user, database, and session-only password when a server layout cannot be detected or the database is remote.", "Manual Database Connection", () => { connectDatabase(); RefreshReadiness(); }), 1, 0);
        workspace.Controls.Add(Card("Configure individual paths", "Override the target profile, server DBCs, client Data, schema, executable, or layer paths individually.", "Advanced Workspace Settings", () => { configurePaths(); RefreshReadiness(); }), 2, 0);
        content.Controls.Add(workspace);
        content.Controls.Add(Heading("Workspace readiness"));
        var readinessBox = new Panel { AutoSize = true, Dock = DockStyle.Top, BackColor = Color.White, Padding = new(16), Margin = new(0, 0, 0, 12), BorderStyle = BorderStyle.FixedSingle };
        readinessBox.Controls.Add(_readiness); content.Controls.Add(readinessBox);
        content.Controls.Add(new Label { Text = "Typical client workflow: edit → save → stage only changed files → review MPQ paths → build → copy into the client Data folder. Backups are created before overwrite operations.", AutoSize = true, MaximumSize = new(1050, 0), ForeColor = Color.FromArgb(75, 85, 99), Margin = new(0, 6, 0, 0) });
        Controls.Add(content); RefreshReadiness();
    }

    public void RefreshReadiness()
    {
        _readiness.Controls.Clear(); _readiness.RowStyles.Clear(); _readiness.RowCount = 0;
        AddReadiness("Detected server workspace", Directory.Exists(_settings.ServerRootPath), _settings.ServerRootPath, "Optional: choose the installed server folder to configure DBC and database paths automatically.");
        AddReadiness("Server DBC folder", Directory.Exists(_settings.CoreDbcPath), _settings.CoreDbcPath, "Set the current core's data\\dbc directory.");
        AddReadiness("Client Data folder", Directory.Exists(_settings.ClientDataPath), _settings.ClientDataPath, "Set the WoW client Data directory.");
        var profiles = TargetProfileCatalog.Load(); var target = TargetProfileCatalog.Find(profiles, _settings.SelectedTargetProfileId);
        AddReadiness($"Target: {target.DisplayName}", true, $"{target.SupportTier}: {target.Notes}", string.Empty);
        AddReadiness("Definition schema", File.Exists(_settings.SchemaDefinitionPath), _settings.SchemaDefinitionPath, $"Select {target.SchemaFileName}.");
        var database = _databaseStatus(); AddReadiness("World database", database is not null, database ?? string.Empty, "Optional: connect and inspect a current AzerothCore/TrinityCore schema for guided server content.");
        AddReadiness("Client executable", File.Exists(_settings.ClientExecutablePath), _settings.ClientExecutablePath, "Optional until protected GlueXML is patched.");
        AddReadiness("Layer comparison", Directory.Exists(_settings.BaseDbcPath) && Directory.Exists(_settings.OverrideDbcPath), $"{_settings.BaseDbcPath} → {_settings.OverrideDbcPath}", "Set both base and override DBC directories.");
    }

    private void AddReadiness(string name, bool ready, string configuredValue, string missingText)
    {
        var row = _readiness.RowCount++;
        _readiness.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _readiness.Controls.Add(new Label { Text = ready ? $"✓  {name}" : $"○  {name}", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = ready ? Color.FromArgb(22, 101, 52) : Color.FromArgb(146, 64, 14), Margin = new(0, 4, 18, 4) }, 0, row);
        _readiness.Controls.Add(new Label { Text = ready ? configuredValue : missingText, AutoSize = true, MaximumSize = new(850, 0), ForeColor = Color.FromArgb(75, 85, 99), Margin = new(0, 4, 0, 4) }, 1, row);
    }

    private static Label Heading(string text) => new() { Text = text, Font = new Font("Segoe UI", 14, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(31, 41, 55), Margin = new(0, 12, 0, 8) };
    private static TableLayoutPanel CardRow()
    {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Margin = new(0, 0, 0, 10) };
        row.ColumnStyles.Add(new(SizeType.Percent, 33.333f)); row.ColumnStyles.Add(new(SizeType.Percent, 33.333f)); row.ColumnStyles.Add(new(SizeType.Percent, 33.334f)); return row;
    }

    private static Control Card(string title, string description, string actionText, Action action)
    {
        var panel = new TableLayoutPanel { RowCount = 3, Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.White, Padding = new(16), Margin = new(0, 0, 12, 0), CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
        panel.RowStyles.Add(new(SizeType.AutoSize)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(31, 41, 55), Margin = new(0, 0, 0, 8) }, 0, 0);
        panel.Controls.Add(new Label { Text = description, AutoSize = true, MaximumSize = new(310, 0), ForeColor = Color.FromArgb(75, 85, 99), Margin = new(0, 0, 0, 12) }, 0, 1);
        var button = new Button { Text = actionText, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new(8, 3, 8, 3) }; button.Click += (_, _) => action(); panel.Controls.Add(button, 0, 2); return panel;
    }
}

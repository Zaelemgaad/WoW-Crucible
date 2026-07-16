using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class DbcSqlAuditForm : Form
{
    private readonly DatabaseConnectionProfile _profile;
    private readonly ServerTableBinding _binding;
    private readonly string _dbcPath;
    private readonly DbcSchemaResolution _schema;
    private readonly DatabaseTableCapability _table;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, VirtualMode = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new(8) };
    private readonly Button _export = new() { Text = "Export DBC → SQL Migration…", AutoSize = true, Enabled = false };
    private DbcSqlAuditResult? _audit;
    private CancellationTokenSource? _cancellation;

    public DbcSqlAuditForm(DatabaseConnectionProfile profile, ServerTableBinding binding, string dbcPath, DbcSchemaResolution schema, DatabaseTableCapability table)
    {
        _profile = profile; _binding = binding; _dbcPath = dbcPath; _schema = schema; _table = table;
        Text = $"Effective Server Data — {binding.DbcFileName}"; Width = 1250; Height = 760; StartPosition = FormStartPosition.CenterParent;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(8), WrapContents = false };
        top.Controls.Add(new Label { Text = $"{binding.Consumption} · {binding.SqlTableName} · {binding.KeyStrategy.Kind} · {binding.Restart}", AutoSize = true, Margin = new(0, 7, 18, 0) });
        top.Controls.Add(_export);
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 15 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Decoded row", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Server DBC", FillWeight = 27 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SQL / effective server", FillWeight = 28 });
        _grid.CellValueNeeded += (_, e) =>
        {
            if (_audit is null || e.RowIndex < 0 || e.RowIndex >= _audit.Rows.Count) return;
            var row = _audit.Rows[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => row.Status, 1 => row.Key, 2 => row.Dimensions, 3 => Format(row.DbcValues), 4 => Format(row.SqlValues), _ => null };
        };
        _grid.RowPrePaint += (_, e) => { if (_audit is not null && _audit.Rows[e.RowIndex].Status is DbcSqlRowStatus.SqlOverridesDbc or DbcSqlRowStatus.MissingDbcRow) _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.MistyRose; };
        _export.Click += ExportMigration;
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(top);
        Shown += async (_, _) => await AuditAsync(); FormClosing += (_, _) => _cancellation?.Cancel();
    }

    private async Task AuditAsync()
    {
        _cancellation = new();
        try
        {
            UseWaitCursor = true; _status.Text = $"Reading {_binding.SqlTableName} and comparing effective rows…";
            _audit = await new DbcSqlAuditService().AuditAsync(_profile, _binding, _dbcPath, _schema, _table, _cancellation.Token);
            _grid.RowCount = _audit.Rows.Count; _grid.Invalidate(); _export.Enabled = _audit.MismatchCount > 0;
            _status.Text = $"{_audit.Rows.Count:N0} rows · {_audit.Rows.Count(row => row.Status == DbcSqlRowStatus.Same):N0} SQL same · {_audit.Rows.Count(row => row.Status == DbcSqlRowStatus.SqlOverridesDbc):N0} SQL overrides · {_audit.Rows.Count(row => row.Status == DbcSqlRowStatus.DbcOnly):N0} DBC-only · SQL overrides only rows present in the overlay · {_binding.Restart}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogger.Log("DBC/SQL audit failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private void ExportMigration(object? sender, EventArgs e)
    {
        if (_audit is null) return;
        using var dialog = new SaveFileDialog { Filter = "SQL migration (*.sql)|*.sql", FileName = $"sync_{Path.GetFileNameWithoutExtension(_dbcPath).ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.sql" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, DbcSqlAuditService.CreateIdempotentMigration(_audit));
        _status.Text = $"Exported previewable, idempotent migration: {dialog.FileName}. No database rows were modified.";
    }

    private static string Format(IReadOnlyDictionary<string, object?> values) => values.Count == 0 ? "<missing>" : string.Join(", ", values.Select(pair => $"{pair.Key}={pair.Value}"));
}

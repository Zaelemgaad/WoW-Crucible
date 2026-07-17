using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DbcDocumentSession(WdbcFile file, DbcSchemaResolution schema, string schemaSource)
{
    public WdbcFile File { get; } = file;
    public DbcSchemaResolution Schema { get; } = schema;
    public string SchemaSource { get; } = schemaSource;
    public DesktopEditHistory History { get; } = new();
    public string FullPath => Path.GetFullPath(File.SourcePath);
    public string DisplayName => Path.GetFileName(File.SourcePath) + (File.IsDirty ? " *" : string.Empty);
    public DbcColumn? IdColumn => DbcRecordIdentity.PhysicalColumn(Schema.Columns, Schema.KeyStrategy);
}

internal sealed class DesktopEditHistory
{
    private const int MaximumActions = 10_000;
    private readonly Stack<CellEdit> _undo = new();
    private readonly Stack<CellEdit> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.TryPeek(out var edit) ? edit.Description : null;
    public string? RedoDescription => _redo.TryPeek(out var edit) ? edit.Description : null;

    public void Record(int row, DbcColumn column, uint before, uint after)
    {
        if (before == after) return;
        _undo.Push(new(row, column, before, after));
        _redo.Clear();
        if (_undo.Count <= MaximumActions) return;
        var retained = _undo.Take(MaximumActions).Reverse().ToArray();
        _undo.Clear();
        foreach (var edit in retained) _undo.Push(edit);
    }

    public CellEdit? Undo(WdbcFile file)
    {
        if (!_undo.TryPop(out var edit)) return null;
        file.SetRaw(edit.Row, edit.Column, edit.Before);
        _redo.Push(edit);
        return edit;
    }

    public CellEdit? Redo(WdbcFile file)
    {
        if (!_redo.TryPop(out var edit)) return null;
        file.SetRaw(edit.Row, edit.Column, edit.After);
        _undo.Push(edit);
        return edit;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    internal sealed record CellEdit(int Row, DbcColumn Column, uint Before, uint After)
    {
        public string Description => $"row {Row + 1:N0}, {Column.Name}";
    }
}

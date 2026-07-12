using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class EditHistory
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
        public string Description => $"row {Row:N0}, {Column.Name}";
    }
}

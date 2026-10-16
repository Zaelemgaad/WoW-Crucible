namespace WoWCrucible.Desktop;

internal sealed record WorkspaceTab(string Id, string Title, bool Pinned = false);

/// <summary>Navigation only. Closing a tab never disposes or saves its editor.</summary>
internal sealed class WorkspaceTabState
{
    private readonly List<WorkspaceTab> _tabs = [new("home", "Home", true), new("tables", "Data tables", true)];
    private readonly List<string> _history = [];
    public IReadOnlyList<WorkspaceTab> Tabs => _tabs;
    public string SelectedId { get; private set; } = "home";

    public void Open(string id, string title)
    {
        var index = _tabs.FindIndex(tab => tab.Id == id);
        if (index < 0) _tabs.Add(new(id, title));
        else _tabs[index] = _tabs[index] with { Title = title };
        Select(id);
    }

    public void Select(string id)
    {
        if (!_tabs.Any(tab => tab.Id == id)) throw new ArgumentException("Workspace is not open.", nameof(id));
        if (SelectedId == id) return;
        _history.Remove(SelectedId);
        _history.Add(SelectedId);
        _history.Remove(id);
        SelectedId = id;
    }

    public void Back()
    {
        if (_history.Count == 0) { Select("home"); return; }
        SelectedId = _history[^1];
        _history.RemoveAt(_history.Count - 1);
    }

    public void Close(string id)
    {
        var index = _tabs.FindIndex(tab => tab.Id == id);
        if (index < 0 || _tabs[index].Pinned) return;
        _tabs.RemoveAt(index);
        _history.Remove(id);
        if (SelectedId == id) Back();
    }

    public void Move(string id, int delta)
    {
        var index = _tabs.FindIndex(tab => tab.Id == id);
        if (index < 0 || _tabs[index].Pinned) return;
        var destination = Math.Clamp(index + delta, 2, _tabs.Count - 1);
        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        _tabs.Insert(destination, tab);
    }
}

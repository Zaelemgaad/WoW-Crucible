using System.Reflection;
using Avalonia.Platform.Storage;

// Test-only interface proxy: exercise picker cancellation without opening OS UI.
// Avalonia reserves direct interface implementation for its platform providers.
internal class CoverageStorageProvider : DispatchProxy
{
    public static IStorageProvider Create() => Create<IStorageProvider, CoverageStorageProvider>();
    public int Calls { get; private set; }
    public FilePickerOpenOptions? LastOpenOptions { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_CanOpen": case "get_CanSave": case "get_CanPickFolder": return true;
            case "OpenFilePickerAsync": Calls++; LastOpenOptions = (FilePickerOpenOptions)args![0]!; return Task.FromResult<IReadOnlyList<IStorageFile>>([]);
            case "OpenFilePickerWithResultAsync": Calls++; return Task.FromResult(new OpenFilePickerResult());
            case "SaveFilePickerAsync": Calls++; return Task.FromResult<IStorageFile?>(null);
            case "SaveFilePickerWithResultAsync": Calls++; return Task.FromResult(new SaveFilePickerResult());
            case "OpenFolderPickerAsync": Calls++; return Task.FromResult<IReadOnlyList<IStorageFolder>>([]);
            case "OpenFileBookmarkAsync": return Task.FromResult<IStorageBookmarkFile?>(null);
            case "OpenFolderBookmarkAsync": return Task.FromResult<IStorageBookmarkFolder?>(null);
            case "TryGetFileFromPathAsync": return Task.FromResult<IStorageFile?>(null);
            case "TryGetFolderFromPathAsync": case "TryGetWellKnownFolderAsync": return Task.FromResult<IStorageFolder?>(null);
            default: throw new NotSupportedException("Unexpected picker API: " + method.Name);
        }
    }
}

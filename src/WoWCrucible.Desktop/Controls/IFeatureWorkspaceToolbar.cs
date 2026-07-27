using Avalonia.Controls;

namespace WoWCrucible.Desktop.Controls;

/// <summary>
/// Lets an embedded workspace contribute contextual actions to the one shared
/// application toolbar. Workspaces must keep their editing content in the main
/// pane instead of creating another permanent navigation/header row.
/// </summary>
internal interface IFeatureWorkspaceToolbar
{
    Control? FeatureToolbar { get; }
}

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;

namespace WoWCrucible.Desktop.WindowsIntegration;

internal sealed class ExplorerIntegrationSettings : StackPanel
{
    public ExplorerIntegrationSettings()
    {
        Spacing = 8;
        if (!OperatingSystem.IsWindows()) { IsVisible = false; return; }
        var enabled = new CheckBox { Content = "Crucible in the Explorer right-click menu" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var report = new Button { Content = "Open last export results" };
        Children.Add(enabled);
        Children.Add(report);
        Children.Add(status);
        try { enabled.IsChecked = ExplorerMenuRegistration.IsInstalled; }
        catch (Exception exception) { status.Text = exception.Message; }
        var updating = false;
        enabled.IsCheckedChanged += (_, _) =>
        {
            if (updating || !OperatingSystem.IsWindows()) return;
            try
            {
                if (enabled.IsChecked == true) ExplorerMenuRegistration.Install();
                else ExplorerMenuRegistration.Remove();
                status.Text = enabled.IsChecked == true ? "Enabled for DBC and DB2 files." : "Explorer menu removed.";
            }
            catch (Exception exception)
            {
                updating = true;
                enabled.IsChecked = !enabled.IsChecked;
                updating = false;
                status.Text = exception.Message;
                DesktopCrashLogger.Failure("EXPLORER", "registration-failed", exception);
            }
        };
        report.Click += (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                if (!File.Exists(ExplorerReports.PathName)) { status.Text = "No Explorer exports have run yet."; return; }
                Process.Start(new ProcessStartInfo(ExplorerReports.PathName) { UseShellExecute = true });
            }
            catch (Exception exception) { status.Text = exception.Message; }
        };
    }
}

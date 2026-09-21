using Microsoft.Win32;
using System.Runtime.Versioning;

namespace WoWCrucible.Desktop.WindowsIntegration;

internal enum ExplorerAction { Open, Csv, Json }
internal sealed record ExplorerVerb(string Key, string Label, Guid ClassId, ExplorerAction Action);

[SupportedOSPlatform("windows")]
internal static class ExplorerMenuRegistration
{
    private const string Classes = @"Software\Classes\";
    private const string Menu = "WoWCrucible.Explorer";
    private const string Owner = "WoWCrucible.Explorer.v1";
    internal static readonly ExplorerVerb[] Verbs =
    [
        new("01open", "Open", new("86515F81-E7C1-4D11-92AD-621FDDB746C8"), ExplorerAction.Open),
        new("02csv", "Convert to CSV", new("F89EBC30-9206-4C4B-B222-DB59B290A935"), ExplorerAction.Csv),
        new("03json", "Convert to JSON", new("DA5858C0-88A2-48F6-B251-935928864C0F"), ExplorerAction.Json)
    ];
    private static readonly string[] Extensions = [".dbc", ".db2"];

    internal static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Classes + Menu);
            return key?.GetValue("CrucibleOwner") as string == Owner;
        }
    }

    internal static void Install()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the Crucible executable.");
        if (!Path.GetFileName(executable).Equals("WoWCrucible.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install the Explorer menu from WoWCrucible.Desktop.exe, not the dotnet host.");
        var roots = Extensions.Select(extension => $@"SystemFileAssociations\{extension}\shell\WoWCrucible")
            .Append(Menu).Concat(Verbs.Select(verb => $@"CLSID\{verb.ClassId:B}")).ToArray();
        foreach (var root in roots) VerifyOwnership(root);
        foreach (var verb in Verbs)
        {
            using var clsid = OwnedKey($@"CLSID\{verb.ClassId:B}");
            clsid.SetValue("", $"Crucible - {verb.Label}");
            using var server = clsid.CreateSubKey("LocalServer32");
            server.SetValue("", $"\"{executable}\" --explorer-server");
            server.SetValue("ServerExecutable", executable);
        }
        using (var menu = OwnedKey(Menu))
        {
            foreach (var verb in Verbs)
            {
                using var key = menu.CreateSubKey($@"shell\{verb.Key}");
                key.SetValue("MUIVerb", verb.Label);
                key.SetValue("MultiSelectModel", "Player");
                using var command = key.CreateSubKey("command");
                command.SetValue("DelegateExecute", verb.ClassId.ToString("B"));
            }
        }
        foreach (var extension in Extensions)
        {
            using var key = OwnedKey($@"SystemFileAssociations\{extension}\shell\WoWCrucible");
            key.SetValue("MUIVerb", "Crucible");
            key.SetValue("Icon", $"\"{executable}\",0");
            key.SetValue("MultiSelectModel", "Player");
            key.SetValue("ExtendedSubCommandsKey", Menu);
        }
        ExplorerNative.SHChangeNotify(0x08000000, 0, 0, 0);
    }

    internal static void Remove()
    {
        var roots = Extensions.Select(extension => $@"SystemFileAssociations\{extension}\shell\WoWCrucible")
            .Append(Menu).Concat(Verbs.Select(verb => $@"CLSID\{verb.ClassId:B}")).ToArray();
        foreach (var root in roots) VerifyOwnership(root);
        foreach (var root in roots) Registry.CurrentUser.DeleteSubKeyTree(Classes + root, throwOnMissingSubKey: false);
        ExplorerNative.SHChangeNotify(0x08000000, 0, 0, 0);
    }

    private static void VerifyOwnership(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Classes + path);
        if (key is not null && key.GetValue("CrucibleOwner") as string != Owner)
            throw new InvalidOperationException($"The registry key {path} belongs to another registration; it was not changed.");
    }

    private static RegistryKey OwnedKey(string path)
    {
        var key = Registry.CurrentUser.CreateSubKey(Classes + path);
        key.SetValue("CrucibleOwner", Owner);
        return key;
    }
}

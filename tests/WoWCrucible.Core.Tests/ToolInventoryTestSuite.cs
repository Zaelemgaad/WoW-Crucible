using WoWCrucible.Core;

internal static class ToolInventoryTestSuite
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crucible-tool-layout-{Guid.NewGuid():N}");
        try
        {
            var app = Path.Combine(root, "Tools", "WoW Crucible");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "WoWCrucible.slnx"), "<Solution />");
            Directory.CreateDirectory(Path.Combine(root, "Game Clients"));
            Directory.CreateDirectory(Path.Combine(root, "Tools", "Legacy Tools", "Map", "FuTa"));
            Directory.CreateDirectory(Path.Combine(root, "Tools", "Legacy Tools", "Textures", "LegacyTextureTool"));
            File.WriteAllBytes(Path.Combine(root, "Tools", "LooseTool.exe"), []);
            File.WriteAllText(Path.Combine(root, "Tools", "notes.txt"), "Not a tool");
            Directory.CreateDirectory(Path.Combine(root, "Tools", "Coffee Working Copy", "Tools", "M2", "UnknownModelUtility"));
            Directory.CreateDirectory(Path.Combine(root, "Tools", "Obsolete", "WMOListFile"));
            Directory.CreateDirectory(Path.Combine(root, "Tools", "MysteryTool"));
            Directory.CreateDirectory(Path.Combine(root, "Tools", "MPQ Editor"));
            Directory.CreateDirectory(Path.Combine(root, "Tools", "WoW Model Viewer Web"));
            Directory.CreateDirectory(Path.Combine(app, ".local", "Models", "NotAnInstalledTool"));
            var report = ToolConsolidationInventoryService.Scan(root);
            if (!ToolConsolidationInventoryService.FindWorkspaceRoot(app).Equals(root, StringComparison.OrdinalIgnoreCase) ||
                report.Missing != 0 || report.Entries.Any(entry => entry.RelativePath.Contains("Game Clients") || entry.RelativePath.Contains("NotAnInstalledTool")) ||
                !report.Entries.Any(entry => entry.RelativePath == "Tools/Legacy Tools/Map/FuTa" && entry.Status == ToolInventoryStatus.Tracked) ||
                !report.Entries.Any(entry => entry.RelativePath == "Tools/Legacy Tools/Textures/LegacyTextureTool") ||
                !report.Entries.Any(entry => entry.RelativePath == "Tools/LooseTool.exe" && entry.Status == ToolInventoryStatus.Unassigned) ||
                report.Entries.Any(entry => entry.RelativePath == "Tools/notes.txt") ||
                !report.Entries.Any(entry => entry.RelativePath == "Tools/Obsolete/WMOListFile" && entry.Scope == "Obsolete") ||
                !report.Entries.Any(entry => entry.RelativePath == "Tools/MPQ Editor" && entry.Status == ToolInventoryStatus.Tracked) ||
                !report.Entries.Any(entry => entry.RelativePath == "Tools/WoW Model Viewer Web" && entry.Capability.Contains("Miorey")) ||
                !report.Entries.Any(entry => entry.RelativePath.EndsWith("UnknownModelUtility") && entry.Status == ToolInventoryStatus.Unassigned) ||
                !report.Entries.Any(entry => entry.RelativePath == "Tools/MysteryTool" && entry.Status == ToolInventoryStatus.Unassigned))
                throw new InvalidOperationException("Consolidated tool discovery lost collections, obsolete tools, or unknown additions.");

            var external = Path.Combine(root, "outside");
            Directory.CreateDirectory(Path.Combine(external, "AmarothTools", "NPCGenerator"));
            var link = Path.Combine(root, "Tools", "Legacy Linked Tools");
            if (OperatingSystem.IsWindows())
            {
                using var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/d /c mklink /J \"{link}\" \"{external}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }) ?? throw new InvalidOperationException("Cannot start junction fixture creation.");
                result.WaitForExit();
                if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError.ReadToEnd());
            }
            else Directory.CreateSymbolicLink(link, external);
            try
            {
                if (ToolConsolidationInventoryService.Scan(root).Entries.Any(entry => entry.FullPath.StartsWith(link, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Tool discovery followed a linked collection.");
            }
            finally { Directory.Delete(link); }
            Console.WriteLine("PASS tool inventory: relocated workspace, nested collections, obsolete tools, unknown additions, and link boundaries.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

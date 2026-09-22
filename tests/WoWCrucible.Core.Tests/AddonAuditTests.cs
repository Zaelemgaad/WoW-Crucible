using WoWCrucible.Core;

internal static class AddonAuditTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "crucible-addon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Put("one/Core/Core.toc", "## Interface: 30300\n## SavedVariables: CoreDB\n## SavedVariablesPerCharacter: LocalDB\n## Dependencies: Module, Blizzard_TalentUI\nmain.xml\n");
            Put("one/Core/main.xml", "<Ui><Include file=\"lib/files.xml\"/></Ui>");
            Put("one/Core/lib/files.xml", "<Ui><Script file=\"body.lua\"/></Ui>");
            Put("one/Core/lib/body.lua", "local a = 1");
            Put("one/Core/lib/Unused/Unused.toc", "## Interface: 30300");
            Put("one/Core/.git/HEAD", "unique addon source history");
            Put("one/Module/Module.toc", "## Interface: 30300");
            foreach (var file in Directory.GetFiles(Path.Combine(root, "one/Core"), "*", SearchOption.AllDirectories))
                Put("two/Core/" + Path.GetRelativePath(Path.Combine(root, "one/Core"), file), File.ReadAllText(file));
            Put("broken/Broken/Broken.toc", "## Interface: 100000\nmissing.lua\nloop.xml\n../outside.lua\nbad\0file.lua\nvalid.lua");
            Put("broken/Broken/valid.lua", "local survives = true");
            Put("broken/Broken/loop.xml", "<Ui><Include file=\"loop.xml\"/></Ui>");
            Put("excluded/Bad/Bad.toc", "## Interface: 30300\nabsent.lua");
            Put("flavor/Only/Only_Wrath.toc", "## Interface: 30300");
            var report = AddonAuditService.Scan(root, exclusions: [Path.Combine(root, "excluded")]);
            Require(report.Packages.Count == 5, "Nested embedded libraries and exclusions must not become standalone addons.");
            var cores = report.Packages.Where(package => package.Name == "Core").ToArray();
            Require(cores[0].ContentSha256 == cores[1].ContentSha256, "Exact folder copies must match independently of installation path.");
            Require(cores[0].Errors == 0 && cores[0].LoadFiles.Contains("lib/body.lua"), "Relative XML includes must resolve against their own directory.");
            Require(cores[0].SavedVariables.SequenceEqual(["CoreDB"]) && cores[0].CharacterVariables.SequenceEqual(["LocalDB"]), "Variable scopes must remain separate.");
            Require(!cores[0].Issues.Any(issue => issue.Code == "MissingDependency") && cores[1].Issues.Count(issue => issue.Code == "MissingDependency") == 1, "Dependency context must be per installation, with Blizzard provided by the client.");
            var broken = report.Packages.Single(package => package.Name == "Broken");
            Require(new[] { "MissingFile", "IncludeCycle", "InterfaceTarget", "ExternalReference", "InvalidReference" }.All(code => broken.Issues.Any(issue => issue.Code == code)), "Broken references and cycles must be reported, never followed outside the package.");
            Require(broken.LoadFiles.Contains("valid.lua"), "A malformed reference must not abort the rest of the package or scan.");
            Require(report.Packages.Single(package => package.Name == "Only").Issues.Any(issue => issue.Code == "ManifestName"), "A renamed/flavored TOC is not silently installable in 3.3.5.");
            Put("two/Core/lib/body.lua", "local a = 2");
            Require(AddonAuditService.Scan(Path.Combine(root, "two")).Packages[0].ContentSha256 != cores[0].ContentSha256, "A one-file change must make a distinct variant.");
            var partial = AddonAuditService.Scan(Path.Combine(root, "one"), exclusions: [Path.Combine(root, "one/Core/lib")]).Packages.Single(package => package.Name == "Core");
            Require(partial.ContentSha256.Length == 0 && !partial.Files.Any(file => file.Path.StartsWith("lib/")) && !partial.LoadFiles.Contains("lib/body.lua"), "Exclusions inside a package must be honored for both hashing and load references, without a false full-package fingerprint.");
            Put("list.toc", "## SavedVariables: First\n## savedvariables: Second\n");
            Require(AddonAuditService.Parse(Path.Combine(root, "list.toc")).Metadata["SavedVariables"] == "First, Second", "Repeated list metadata must merge case-insensitively.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { AddonAuditService.Scan(root, cancellationToken: canceled.Token); throw new Exception("Canceled scan ran."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Addon audit tests passed.");

        void Put(string path, string value) { path = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}

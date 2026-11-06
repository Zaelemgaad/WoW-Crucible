using WoWCrucible.Core;

internal static class CustomizationExtensionTestSuite
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Customization extensions: " + message);
    }

    private static string NewCatalog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crucible-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "merged_model.tsv"), "chr_model\tname\tsex\tsource\n268435457\tHuman male\t0\t1\n");
        File.WriteAllText(Path.Combine(dir, "merged_option.tsv"), "option\tchr_model\n268435465\t268435457\n");
        File.WriteAllText(Path.Combine(dir, "merged_choice.tsv"), "choice\toption\n268435556\t268435465\n");
        File.WriteAllText(Path.Combine(dir, "wardrobe_subject.tsv"), "subject\tname\tcategory\tnative_display\tmatch\tbody\n536871101\tBear Form\t17\t2281\tform:5;form:8\t\n");
        File.WriteAllText(Path.Combine(dir, "wardrobe_option.tsv"), "option\tchr_model\n536871813\t536871101\n");
        File.WriteAllText(Path.Combine(dir, "wardrobe_choice.tsv"), "choice\toption\n536878000\t536871813\n");
        return dir;
    }

    public static void Run()
    {
        Require(CustomizationExtensionService.ParseId("u12") == 0xF000000Cu, "u12");
        Require(CustomizationExtensionService.ParseId("r966") == (2u << 28 | 966), "r966");
        Require(CustomizationExtensionService.ParseId("e966") == (1u << 28 | 966), "e966");
        Require(CustomizationExtensionService.ParseId("268435469") == 268435469u, "whole id");
        foreach (var bad in new[] { "", "u", "u0", "x1", "u268435456", "0" }) Require(CustomizationExtensionService.ParseId(bad) is null, $"`{bad}` is no id");
        Require(CustomizationExtensionService.FormatId(0xF000000Cu) == "u12" && CustomizationExtensionService.FormatId(2u << 28 | 966) == "r966", "ids format back");

        var dir = NewCatalog();
        try
        {
            // A good bear choice goes in, numbered, and validates.
            var id = CustomizationExtensionService.AddRow(dir, "choices.tsv", new Dictionary<string, string>
            {
                ["option"] = "r901", ["name"] = "Moon Bear", ["model"] = "creature/bear/bear.m2", ["texture1"] = "creature/bear/moon.blp", ["scale"] = "1.2",
            }, out var problems);
            Require(id == "u1" && !problems.Any(p => p.Severity == "ERROR"), $"bear row: {id} {string.Join("; ", problems)}");
            var second = CustomizationExtensionService.AddRow(dir, "choices.tsv", new Dictionary<string, string> { ["option"] = "r901", ["name"] = "Black", ["display"] = "2281" }, out _);
            Require(second == "u2", "the next id is free");

            // A row on an option nothing holds is refused, and the file is as it was.
            var before = File.ReadAllText(Path.Combine(dir, "user", "choices.tsv"));
            var refused = CustomizationExtensionService.AddRow(dir, "choices.tsv", new Dictionary<string, string> { ["option"] = "r424242", ["name"] = "Orphan", ["display"] = "1" }, out problems);
            Require(refused.Length == 0 && problems.Any(p => p.Message.Contains("neither the catalog")), "an orphan is refused");
            Require(File.ReadAllText(Path.Combine(dir, "user", "choices.tsv")) == before, "a refused row leaves the file");

            // Each kind of bad row is named.
            File.AppendAllText(Path.Combine(dir, "user", "choices.tsv"), "u1\tr901\tTwice\t\t12\t\t\t\t\t\t\t\t\n" + "u9\tr901\tEmpty\t\t\t\t\t\t\t\t\t\t\n" + "u10\tr901\tBoth\t\t12\ta.m2\t\t\t\t\t\t\t\n" + "u11\tr901\tBad\t\t\ta.m2\t\t\t\t-1\tx\t7\tzz\n");
            File.WriteAllText(Path.Combine(dir, "user", "subjects.tsv"), "subject\tname\tcategory\tnative_display\tmatch\tbody\nu1\tWolf\t24\t\tshape:3\t\nu2\tGood\t24\t4613\tform:16\tcreature/wolf.m2\n");
            File.WriteAllText(Path.Combine(dir, "user", "options.tsv"), "option\tbody\tname\tcategory\torder\nr5\tu7\t\t\tx\n");
            File.WriteAllText(Path.Combine(dir, "user", "items.tsv"), "appearance\tcategory\titem\tname\tcolour\nu1\t\t\tX\tred\n");
            problems = CustomizationExtensionService.Validate(dir);
            foreach (var expected in new[]
            {
                "used twice", "draw nothing new", "The display would win", "not a positive number", "is not xxyy", "is not target=file", "is not RRGGBB",
                "native_display is required", "is not form:", "must be a user id", "neither the catalog nor subjects", "name is empty", "category is empty", "order `x`", "Unknown column `colour`", "item names no item",
            })
                Require(problems.Any(p => p.Message.Contains(expected)), $"expected a problem naming `{expected}`: {string.Join("; ", problems)}");
            Require(!problems.Any(p => p.Message.Contains("u2") && p.File == "subjects.tsv"), "a good subject is not blamed");

            // Export refuses a bad folder, and copies a good one.
            var target = Path.Combine(dir, "server");
            Require(CustomizationExtensionService.Export(dir, target, out _).Count == 0, "a bad folder does not export");
            foreach (var file in new[] { "subjects.tsv", "options.tsv", "items.tsv" }) File.Delete(Path.Combine(dir, "user", file));
            File.WriteAllText(Path.Combine(dir, "user", "choices.tsv"), before);
            var written = CustomizationExtensionService.Export(dir, target, out _);
            Require(written.Count == 1 && File.ReadAllText(written[0]) == before, "a good folder exports");

            // Assets are checked in the folders given, as advice.
            problems = CustomizationExtensionService.Validate(dir, [dir]);
            Require(problems.Any(p => p.Severity == "WARN" && p.Message.Contains("not in a loose folder")), "a missing file is advised of");

            var lines = CustomizationExtensionService.DescribeCatalog(dir);
            Require(lines.Count == 1 && lines[0].Contains("r189\tBear Form") && lines[0].Contains("r901"), "the catalog names its options");
        }
        finally { Directory.Delete(dir, recursive: true); }
        Console.WriteLine("Customization extension tests passed.");
    }
}

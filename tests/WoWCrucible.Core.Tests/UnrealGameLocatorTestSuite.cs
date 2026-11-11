using WoWCrucible.Core;

internal static class UnrealGameLocatorTestSuite
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Unreal game locator: " + message);
    }

    public static void Run()
    {
        Require(UnrealGameLocator.Fold("DRAGON BALL: Sparking! ZERO") == "dragonballsparkingzero", "fold drops case and punctuation");
        Require(UnrealGameLocator.Fold("Stellar Blade™") == "stellarblade", "fold drops symbols");

        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n";
        var paths = UnrealGameLocator.VdfValues(vdf, "path").ToList();
        Require(paths.SequenceEqual([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"]), "library paths unescaped: " + string.Join(" | ", paths));
        Require(UnrealGameLocator.VdfValues("\"name\"\t\t\"Stellar Blade\"\n\"installdir\"\t\t\"StellarBlade\"", "installdir").Single() == "StellarBlade", "acf installdir");

        Require(UnrealGameLocator.GameRootOf(@"D:\Games\Fortnite\FortniteGame\Content\Paks") == @"D:\Games\Fortnite", "FModel's Paks folder is the game's");
        Require(UnrealGameLocator.GameRootOf(@"E:\Steam\common\Expedition 33") == @"E:\Steam\common\Expedition 33", "a game folder stays");

        var games = new[]
        {
            new UnrealGame("Stellar Blade Demo", @"D:\Demo", "SB", @"D:\Demo\SB\Content\Paks", 10),
            new UnrealGame("Stellar Blade", @"C:\Full", "SB", @"C:\Full\SB\Content\Paks", 1000),
            new UnrealGame("Other", @"C:\Other", "Other", @"C:\Other\Other\Content\Paks", 5000),
        };
        var found = UnrealGameLocator.ForProject("sb", games);
        Require(found.Count == 2 && found[0].Name == "Stellar Blade", "the full game before the demo");

        var file = Path.Combine(Path.GetTempPath(), "crucible-game-settings-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(file, UnrealModSettings.Template +
                "Stellar Blade key: 0xAAAA\n" +
                "DRAGON BALL Sparking! ZERO key 0123456789abcdef0123456789abcdef: 0xBBBB\n" +
                "dragon ball sparking zero mappings: \"D:\\Maps\\SZ.usmap\"\n" +
                "My Game folder: E:\\Games\\Mine\n" +
                "this line means nothing\n");
            var settings = UnrealModSettings.Load(file);
            Require(settings.Keys("Stellar Blade™").Single() == ("", "0xAAAA"), "a main key by the game's name");
            Require(settings.Keys("DRAGON BALL: Sparking! ZERO", "SparkingZERO").Single() == ("0123456789abcdef0123456789abcdef", "0xBBBB"), "a key by guid");
            Require(settings.Value("mappings", "DRAGON BALL: Sparking! ZERO") == @"D:\Maps\SZ.usmap", "mappings, quotes trimmed");
            Require(settings.Folders().Single() == ("My Game", @"E:\Games\Mine"), "a named folder");
            Require(settings.Keys("Other").Count == 0 && UnrealModSettings.Load(file + ".missing").Keys("Stellar Blade").Count == 0, "nothing for others");
        }
        finally
        {
            File.Delete(file);
        }

        // The community key list: sections, key formats, variants, bad lines. Made-up keys.
        var hex = (char c) => "0x" + new string(c, 64);
        var keyFile = Path.Combine(Path.GetTempPath(), "crucible-aes-keys-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(keyFile,
                "﻿    Absolver    " + Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()) + "\n" +
                "    Dragon Ball FighterZ    " + hex('1') + "\n" +
                "    Dragon Ball: Sparking! Zero (switch)    " + hex('2') + "\n" +
                "    Dragon Ball: Sparking! Zero (PC)    " + hex('3') + "\n" +
                "    Old Game\t" + new string('a', 32) + "\n" +
                "    Wuthering Waves (old keys)  see here: https://example.invalid/keys\n" +
                "    Broken Game 0x1234\n" +
                "\t\n\tUSM KEYS\n" +
                "\tDragon Ball: Sparking! Zero 0123456789ABCDEF\n");
            var list = AesKeyList.Load(keyFile);
            Require(list.Entries.Count == 5, "five pak keys: " + string.Join(" | ", list.Entries.Select(e => e.Name)));
            Require(list.Skipped == 2, "a note and a fragment are passed over");
            Require(list.Other.Single() == ("USM KEYS", "Dragon Ball: Sparking! Zero"), "a video key is kept apart");
            Require(list.Entries[0].Key.All(b => b == 7) && list.Entries[4].Key.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(new string('a', 32))), "base64 and an old 32-character key");
            var sz = list.Candidates("DRAGON BALL Sparking! ZERO", "SparkingZERO");
            Require(sz.Select(c => c.Entry).SequenceEqual(["Dragon Ball: Sparking! Zero (PC)", "Dragon Ball: Sparking! Zero (switch)"]), "PC before switch, no video key: " + string.Join(" | ", sz.Select(c => c.Entry)));
            Require(sz[0].Key.All(b => b == 0x33), "the PC key's bytes");
            Require(list.Candidates("Stellar Blade™", "SB").Count == 0, "nothing for a game not listed");
            Require(AesKeyList.Load(keyFile + ".missing") is { Path: null, Entries.Count: 0 }, "no list");
        }
        finally
        {
            File.Delete(keyFile);
        }
    }
}

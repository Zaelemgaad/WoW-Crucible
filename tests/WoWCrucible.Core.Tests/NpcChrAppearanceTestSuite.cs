using WoWCrucible.Core;

internal static class NpcChrAppearanceTestSuite
{
    public static void Run(string schemaPath, string dbcDirectory)
    {
        var creatureSchemas = DbcSchemaCatalog.Load(schemaPath);
        var npcChrRoot = Path.Combine(Path.GetTempPath(), $"crucible-npc-chr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(npcChrRoot);
        try
        {
            var npcChrPath = Path.Combine(npcChrRoot, "human-male.chr");
            File.WriteAllLines(npcChrPath,
            [
                @"Character/Human/Male/HumanMale.m2",
                "1 0",
                "2 3 4 5 6 7",
                "1000", "777", "0", "0", "0", "0", "0", "0", "0", "0", "2000", "3000", "0", "0", "4000",
                "preserved trailing WMV field"
            ]);
            var npcTexturePath = Path.Combine(npcChrRoot, "human-male.png");
            BlpTextureService.WritePng(npcTexturePath, new RgbaTexture(4, 4, Enumerable.Repeat(new byte[] { 80, 120, 160, 255 }, 16).SelectMany(pixel => pixel).ToArray()));
            var parsedNpcChr = NpcChrAppearanceService.Parse(npcChrPath);
            if (parsedNpcChr.ModelPath != @"Character\Human\Male\HumanMale.m2" || parsedNpcChr.RaceId != 1 || parsedNpcChr.SexId != 0 || parsedNpcChr.Equipment.Head != 1000 || parsedNpcChr.Equipment.RightHand != 2000 || parsedNpcChr.Equipment.Quiver != 4000 || parsedNpcChr.TrailingLines.Count != 1)
                throw new InvalidOperationException("Strict WMV .chr parsing lost model, identity, appearance, equipment, or trailing review data.");
            var npcPlan = NpcChrAppearanceService.CreatePlan(npcChrPath, npcTexturePath, dbcDirectory, schemaPath, new Dictionary<uint, uint> { [1000] = 8815 });
            if (!npcPlan.Ready || npcPlan.ModelId != 49 || npcPlan.ReusesExtra || npcPlan.ReusesDisplay || npcPlan.AddedRows != 2 || npcPlan.ItemDisplays.Single(item => item.Slot == "Head").ItemDisplayId != 8815 || npcPlan.WeaponItemEntries["RightHand"] != 2000 || !npcPlan.Findings.Any(finding => finding.Contains("creature_equip_template", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"WMV .chr planning did not resolve HumanMale through real CreatureModelData, preserve weapon entries, or allocate an additive display/extra pair: ready={npcPlan.Ready}, model={npcPlan.ModelId}, rows={npcPlan.AddedRows}.");
            var npcPlanPath = Path.Combine(npcChrRoot, "npc.plan.json"); NpcChrAppearanceService.SavePlan(npcPlanPath, npcPlan); var loadedNpcPlan = NpcChrAppearanceService.LoadPlan(npcPlanPath);
            var npcOutput = Path.Combine(npcChrRoot, "output"); var npcResult = NpcChrAppearanceService.Apply(loadedNpcPlan, npcOutput);
            if (!File.Exists(npcResult.PatchPath) || !File.Exists(npcResult.ManifestPath) || !File.Exists(npcResult.BakedTexturePath) || npcResult.OutputDbcFiles.Count != 2 || !npcResult.OutputDbcFiles.ContainsKey("CreatureDisplayInfo") || !npcResult.OutputDbcFiles.ContainsKey("CreatureDisplayInfoExtra") || !PatchManifestService.Validate(PatchManifestService.Load(npcResult.ManifestPath), npcResult.PatchPath).Passed)
                throw new InvalidOperationException("WMV .chr apply did not produce both additive DBCs, a validated baked BLP, strict manifest, and ready tiny MPQ.");
            var generatedNpcDisplay = WdbcFile.Load(npcResult.OutputDbcFiles["CreatureDisplayInfo"]); var generatedNpcDisplayColumns = creatureSchemas.ResolveColumns("CreatureDisplayInfo", generatedNpcDisplay.FieldCount).Columns;
            var generatedNpcDisplayId = generatedNpcDisplayColumns.First(column => column.Name == "ID"); var generatedNpcDisplayExtra = generatedNpcDisplayColumns.First(column => column.Name == "ExtendedDisplayInfoID");
            var generatedNpcDisplayRow = Enumerable.Range(0, generatedNpcDisplay.RowCount).Single(row => generatedNpcDisplay.GetRaw(row, generatedNpcDisplayId) == npcPlan.DisplayId);
            if (generatedNpcDisplay.GetRaw(generatedNpcDisplayRow, generatedNpcDisplayExtra) != npcPlan.ExtraId)
                throw new InvalidOperationException("Generated CreatureDisplayInfo did not reference the exact generated CreatureDisplayInfoExtra identity.");
            var generatedNpcExtra = WdbcFile.Load(npcResult.OutputDbcFiles["CreatureDisplayInfoExtra"]); var generatedNpcExtraColumns = creatureSchemas.ResolveColumns("CreatureDisplayInfoExtra", generatedNpcExtra.FieldCount).Columns;
            var generatedNpcExtraId = generatedNpcExtraColumns.First(column => column.Name == "ID"); var generatedNpcHead = generatedNpcExtraColumns.First(column => column.Name == "NPCItemDisplay[0]"); var generatedNpcBake = generatedNpcExtraColumns.First(column => column.Name == "BakeName");
            var generatedNpcExtraRow = Enumerable.Range(0, generatedNpcExtra.RowCount).Single(row => generatedNpcExtra.GetRaw(row, generatedNpcExtraId) == npcPlan.ExtraId);
            if (generatedNpcExtra.GetRaw(generatedNpcExtraRow, generatedNpcHead) != 8815 || Convert.ToString(generatedNpcExtra.GetDisplayValue(generatedNpcExtraRow, generatedNpcBake)) != npcPlan.BakedTextureName)
                throw new InvalidOperationException("Generated CreatureDisplayInfoExtra lost the resolved armor display or content-derived baked texture name.");
            try { _ = NpcChrAppearanceService.Parse(Path.Combine(npcChrRoot, "missing.chr")); throw new InvalidOperationException("A missing .chr file was accepted."); }
            catch (FileNotFoundException) { }
            Console.WriteLine("PASS NPC appearance: WMV parsing, real DBC identities, equipment, baked BLP, manifest, and validated MPQ.");
        }
        finally
        {
            Directory.Delete(npcChrRoot, true);
        }
    }
}

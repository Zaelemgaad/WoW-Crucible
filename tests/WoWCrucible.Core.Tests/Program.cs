using WoWCrucible.Core;
using System.Diagnostics;

if (args.Length != 2)
    throw new ArgumentException("Usage: WoWCrucible.Core.Tests <schema.xml> <dbc-directory>");

var targetProfiles = TargetProfileCatalog.Load(Path.Combine(Path.GetTempPath(), $"crucible-profiles-{Guid.NewGuid():N}"), Path.Combine(Path.GetTempPath(), $"crucible-app-profiles-{Guid.NewGuid():N}"));
if (targetProfiles.Count != 4 || TargetProfileCatalog.Find(targetProfiles, null).ClientBuild != 12340 ||
    targetProfiles.Single(profile => profile.ClientBuild == 15595).SupportTier != TargetSupportTier.Experimental)
    throw new InvalidOperationException("Built-in target profiles are incomplete or the verified default changed.");
var customProfileDirectory = Path.Combine(Path.GetTempPath(), $"crucible-custom-profile-{Guid.NewGuid():N}");
TargetProfileCatalog.SaveTemplate(Path.Combine(customProfileDirectory, "custom.json"), new("custom-9999", "Custom Test Build", "Test", 9999, "custom.xml", ClientTableFormat.Wdbc, ArchiveFormat.Mpq, TargetSupportTier.Experimental, "Fixture"));
var profilesWithCustom = TargetProfileCatalog.Load(customProfileDirectory, Path.Combine(Path.GetTempPath(), $"crucible-empty-{Guid.NewGuid():N}"));
if (profilesWithCustom.Count != 5 || profilesWithCustom.Single(profile => profile.Id == "custom-9999").ClientBuild != 9999)
    throw new InvalidOperationException("External target profile loading failed.");
Directory.Delete(customProfileDirectory, true);

var azerothItemTable = new DatabaseTableCapability("item_template", ItemColumns("entry", "class", "subclass", "name", "displayid", "Quality", "InventoryType", "ItemLevel", "RequiredLevel", "BuyPrice", "SellPrice", "bonding", "Flags", "armor", "dmg_min1", "dmg_max1", "delay", "MaxDurability", "description", "stat_type1", "stat_value1"));
var trinityItemTable = new DatabaseTableCapability("item_template", ItemColumns("entry", "class", "subclass", "name", "displayid", "Quality", "InventoryType", "ItemLevel", "StatsCount", "stat_type1", "stat_value1", "stat_type2", "stat_value2"));
var itemDraft = new ItemDraft(900001, "Crucible's Blade", 2, 8, 123, 4, 17, 200, 80, 10000, 2500, 2, 0, 0, 120, 180, 2800, 120, "Adapter test", 4, 50, 7, 75);
var azerothPlan = ItemTemplateAdapter.CreatePlan(itemDraft, azerothItemTable);
var trinityPlan = ItemTemplateAdapter.CreatePlan(itemDraft, trinityItemTable);
if (!azerothPlan.PreviewSql().Contains("Crucible''s Blade") || trinityPlan.Values["StatsCount"] is not 2 || trinityPlan.Values.ContainsKey("MaxDurability"))
    throw new InvalidOperationException("Capability-aware item mapping or SQL escaping failed.");

static IReadOnlyList<DatabaseColumnCapability> ItemColumns(params string[] names) => names.Select((name, index) => new DatabaseColumnCapability(name, "int", "int", false, "0", name == "entry" ? "PRI" : "", "", index + 1)).ToArray();

var schema = DbcSchemaCatalog.Load(args[0]);
var builtInSchema = DbcSchemaCatalog.CreateBuiltIn12340();
var builtInSpellColumns = builtInSchema.GetColumns("Spell", 234);
if (builtInSpellColumns[136].Name != "Name[enUS]" || builtInSpellColumns[233].Name != "SpellDifficultyID")
    throw new InvalidOperationException("Built-in Spell.dbc schema is incomplete.");
if (builtInSchema.ResolveColumns("NotARealTable", 4).MatchKind != DbcSchemaMatchKind.MissingTableFallback ||
    builtInSchema.ResolveColumns("Spell", 233).MatchKind != DbcSchemaMatchKind.FieldCountMismatchFallback ||
    builtInSchema.ResolveColumns("Spell", 234).MatchKind != DbcSchemaMatchKind.NamedMatch)
    throw new InvalidOperationException("Schema resolution did not expose named/fallback match status.");
var schoolSemantic = DbcSemanticCatalog.Get("Spell", 225) ?? throw new InvalidOperationException("Spell school decoding is missing.");
if (!schoolSemantic.Format(0x44).Contains("Fire") || !schoolSemantic.Format(0x44).Contains("Arcane") || schoolSemantic.Parse("Fire | Arcane") != 0x44)
    throw new InvalidOperationException("Spell school flag decoding/encoding failed.");
var inventorySemantic = DbcSemanticCatalog.Get("Item", 6) ?? throw new InvalidOperationException("Item inventory decoding is missing.");
if (inventorySemantic.Format(17) != "Two-hand weapon [17]" || inventorySemantic.Parse("Two-hand weapon") != 17)
    throw new InvalidOperationException("Item inventory enum decoding/encoding failed.");
var attrSemantic = DbcSemanticCatalog.Get("Spell", 4) ?? throw new InvalidOperationException("Spell attribute decoding is missing.");
const uint combinedAttributes = 0x80000048;
if (attrSemantic.Parse(attrSemantic.Format(combinedAttributes)) != combinedAttributes)
    throw new InvalidOperationException("Decoded spell flags did not round-trip without changing bits.");
var files = Directory.EnumerateFiles(args[1], "*.dbc").ToArray();
if (files.Length == 0) throw new InvalidOperationException("No DBC test files were found.");

var loaded = 0;
var skipped = 0;
foreach (var path in files)
{
    if (new FileInfo(path).Length < WdbcFile.HeaderSize)
    {
        skipped++;
        continue;
    }
    var dbc = WdbcFile.Load(path);
    var columns = schema.GetColumns(Path.GetFileNameWithoutExtension(path), dbc.FieldCount);
    if (columns.Count != dbc.FieldCount) throw new InvalidOperationException($"Column mismatch: {path}");
    if (dbc.RowCount > 0)
    {
        _ = dbc.GetDisplayValue(0, columns[0]);
        _ = dbc.GetDisplayValue(0, columns[^1]);
    }
    loaded++;
}

var spellPath = files.FirstOrDefault(path => Path.GetFileName(path).Equals("Spell.dbc", StringComparison.OrdinalIgnoreCase));
long spellBulkCloneMilliseconds = -1;
if (spellPath is not null)
{
    var spell = WdbcFile.Load(spellPath);
    var output = Path.Combine(Path.GetTempPath(), $"wow-crucible-roundtrip-{Guid.NewGuid():N}.dbc");
    spell.Save(output, false);
    if (!File.ReadAllBytes(spellPath).SequenceEqual(File.ReadAllBytes(output)))
        throw new InvalidOperationException("Unmodified WDBC round trip changed bytes.");
    File.Delete(output);
    var spellColumns = builtInSchema.GetColumns("Spell", spell.FieldCount);
    var spellRowsBefore = spell.RowCount;
    var timer = Stopwatch.StartNew();
    spell.CloneRows(0, 100, spellColumns[0]);
    timer.Stop();
    spellBulkCloneMilliseconds = timer.ElapsedMilliseconds;
    if (spell.RowCount != spellRowsBefore + 100) throw new InvalidOperationException("Real Spell.dbc bulk clone failed.");
}

var animationPath = files.First(path => Path.GetFileName(path).Equals("AnimationData.dbc", StringComparison.OrdinalIgnoreCase));
var animation = WdbcFile.Load(animationPath);
var animationColumns = schema.GetColumns("AnimationData", animation.FieldCount);
var idColumn = animationColumns.First(column => column.IsIndex);
var nameColumn = animationColumns.First(column => column.Type == DbcValueType.StringOffset);
var originalRows = animation.RowCount;
var clone = animation.CloneRow(0, idColumn);
var allocatedId = animation.GetRaw(clone, idColumn);
animation.SetDisplayValue(clone, nameColumn, "WoWCrucible_Test_Ability_Æ");
var editedNameOffset = animation.GetRaw(clone, nameColumn);
var previousNameOffset = animation.GetRaw(0, nameColumn);
animation.SetRaw(clone, nameColumn, previousNameOffset);
animation.SetRaw(clone, nameColumn, editedNameOffset);
var stringSize = animation.StringTableSize;
animation.SetDisplayValue(0, nameColumn, "WoWCrucible_Test_Ability_Æ");
if (animation.StringTableSize != stringSize) throw new InvalidOperationException("String deduplication failed.");
var blank = animation.AddBlankRow(idColumn);
if (animation.GetRaw(blank, idColumn) <= allocatedId) throw new InvalidOperationException("Automatic ID allocation failed.");
animation.DeleteRows([blank]);
if (animation.RowCount != originalRows + 1) throw new InvalidOperationException("Row structural operations failed.");
var editedOutput = Path.Combine(Path.GetTempPath(), $"wow-crucible-edited-{Guid.NewGuid():N}.dbc");
animation.Save(editedOutput, false);
var editedReload = WdbcFile.Load(editedOutput);
if (editedReload.GetString(editedReload.GetRaw(clone, nameColumn)) != "WoWCrucible_Test_Ability_Æ")
    throw new InvalidOperationException("UTF-8 string edit did not survive save/reload.");
File.Delete(editedOutput);
var saveAs = WdbcFile.Load(animationPath);
var saveAsOutput = Path.Combine(Path.GetTempPath(), $"wow-crucible-save-as-{Guid.NewGuid():N}.dbc");
saveAs.SaveAs(saveAsOutput, false);
if (!Path.GetFullPath(saveAsOutput).Equals(saveAs.SourcePath, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Save As did not adopt the new document path.");
File.Delete(saveAsOutput);

var bulk = WdbcFile.Load(animationPath);
var bulkColumns = schema.GetColumns("AnimationData", bulk.FieldCount);
var bulkId = bulkColumns.First(column => column.IsIndex);
var bulkRowsBefore = bulk.RowCount;
var firstBulkRow = bulk.CloneRows(0, 100, bulkId);
if (firstBulkRow != bulkRowsBefore || bulk.RowCount != bulkRowsBefore + 100)
    throw new InvalidOperationException("Bulk clone row count failed.");
for (var index = 1; index < 100; index++)
    if (bulk.GetRaw(firstBulkRow + index, bulkId) != bulk.GetRaw(firstBulkRow, bulkId) + index)
        throw new InvalidOperationException("Bulk clone did not allocate contiguous IDs.");
var bulkOutput = Path.Combine(Path.GetTempPath(), $"wow-crucible-bulk-{Guid.NewGuid():N}.dbc");
bulk.Save(bulkOutput, false);
if (WdbcFile.Load(bulkOutput).RowCount != bulkRowsBefore + 100)
    throw new InvalidOperationException("Bulk-created records did not survive save/reload.");
File.Delete(bulkOutput);

var mpqOutput = Path.Combine(Path.GetTempPath(), $"wow-crucible-patch-{Guid.NewGuid():N}.mpq");
var mapped = PatchInputMapper.Map([animationPath]);
if (mapped.Count != 1 || !mapped[0].ArchivePath.Equals("DBFilesClient\\AnimationData.dbc", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Standalone DBC patch mapping failed.");
var patchService = new PatchArchiveService();
patchService.Create(mpqOutput, mapped);
if (!patchService.Contains(mpqOutput, "DBFilesClient\\AnimationData.dbc"))
    throw new InvalidOperationException("Created MPQ does not contain the mapped DBC path.");
var secondPatchEntry = PatchInputMapper.Map([files.First(path => Path.GetFileName(path).Equals("SpellCastTimes.dbc", StringComparison.OrdinalIgnoreCase))]);
patchService.Update(mpqOutput, secondPatchEntry);
if (!patchService.Contains(mpqOutput, "DBFilesClient\\AnimationData.dbc") || !patchService.Contains(mpqOutput, "DBFilesClient\\SpellCastTimes.dbc"))
    throw new InvalidOperationException("Updating an MPQ did not preserve its existing files.");
var listed = patchService.ListFiles(mpqOutput);
if (!listed.Any(entry => entry.ArchivePath.Equals("DBFilesClient\\AnimationData.dbc", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("MPQ file enumeration did not find a known file.");
var extractRoot = Path.Combine(Path.GetTempPath(), $"wow-crucible-extract-{Guid.NewGuid():N}");
patchService.Extract(mpqOutput, extractRoot, listed.Where(entry => entry.ArchivePath.Equals("DBFilesClient\\SpellCastTimes.dbc", StringComparison.OrdinalIgnoreCase)));
if (!File.Exists(Path.Combine(extractRoot, "DBFilesClient", "SpellCastTimes.dbc")))
    throw new InvalidOperationException("MPQ extraction did not preserve the internal folder path.");
Directory.Delete(extractRoot, true);
File.Delete(mpqOutput);
File.Delete(mpqOutput + ".bak");

var layerRoot = Path.Combine(Path.GetTempPath(), $"wow-crucible-layers-{Guid.NewGuid():N}");
var baseLayer = Path.Combine(layerRoot, "base"); var overrideLayer = Path.Combine(layerRoot, "override"); Directory.CreateDirectory(baseLayer); Directory.CreateDirectory(overrideLayer);
File.Copy(animationPath, Path.Combine(baseLayer, "AnimationData.dbc")); File.Copy(animationPath, Path.Combine(overrideLayer, "AnimationData.dbc"));
var castTimesSource = files.First(path => Path.GetFileName(path).Equals("SpellCastTimes.dbc", StringComparison.OrdinalIgnoreCase));
File.Copy(castTimesSource, Path.Combine(baseLayer, "SpellCastTimes.dbc"));
var castTimesOverride = WdbcFile.Load(castTimesSource); var castColumns = schema.GetColumns("SpellCastTimes", castTimesOverride.FieldCount);
castTimesOverride.SetRaw(0, castColumns[1], castTimesOverride.GetRaw(0, castColumns[1]) + 1); castTimesOverride.Save(Path.Combine(overrideLayer, "SpellCastTimes.dbc"), false);
var durationSource = files.First(path => Path.GetFileName(path).Equals("SpellDuration.dbc", StringComparison.OrdinalIgnoreCase)); File.Copy(durationSource, Path.Combine(overrideLayer, "SpellDuration.dbc"));
var layers = DbcLayerComparer.CompareDirectories(baseLayer, overrideLayer);
if (layers.Single(entry => entry.Name == "AnimationData.dbc").Status != DbcLayerStatus.Identical || layers.Single(entry => entry.Name == "SpellCastTimes.dbc").Status != DbcLayerStatus.Overridden || layers.Single(entry => entry.Name == "SpellDuration.dbc").Status != DbcLayerStatus.OverrideOnly)
    throw new InvalidOperationException("Layer classification failed.");
var layerDetail = DbcLayerComparer.CompareFiles(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns);
if (layerDetail.ModifiedRows != 1 || layerDetail.ModifiedCells != 1) throw new InvalidOperationException("Detailed layered row comparison failed.");

var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try
{
    _ = DbcLayerComparer.CompareFiles(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns, cancelled.Token);
    throw new InvalidOperationException("Cancelled layered comparison completed unexpectedly.");
}
catch (OperationCanceledException) { }

var promotionDifferences = DbcPromotionService.GetDifferences(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns);
if (promotionDifferences.Count != 1 || promotionDifferences[0].Id != WdbcFile.Load(castTimesSource).GetRaw(0, castColumns[0]))
    throw new InvalidOperationException("ID-keyed promotion differences were incorrect.");
var promotionOperation = new DbcPromotionOperation(promotionDifferences[0].Id, [promotionDifferences[0].ColumnName]);
var promotionManifestPath = Path.Combine(layerRoot, "cast-times.crucible-promotion.json");
DbcPromotionService.SaveManifest(promotionManifestPath, "SpellCastTimes", castColumns[0].Name, [promotionOperation]);
var promotionManifest = DbcPromotionService.LoadManifest(promotionManifestPath);
var promotedCastTimesPath = Path.Combine(layerRoot, "SpellCastTimes.promoted.dbc");
DbcPromotionService.Apply(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), promotedCastTimesPath, castColumns, promotionManifest);
var promotedCastTimes = WdbcFile.Load(promotedCastTimesPath);
if (promotedCastTimes.GetRaw(0, castColumns[1]) != castTimesOverride.GetRaw(0, castColumns[1]))
    throw new InvalidOperationException("Selected field promotion did not persist.");

var semanticBasePath = Path.Combine(layerRoot, "AnimationData.semantic-base.dbc");
var semanticOverridePath = Path.Combine(layerRoot, "AnimationData.semantic-override.dbc");
var semanticBase = WdbcFile.Load(animationPath); semanticBase.SetDisplayValue(0, nameColumn, "Crucible_Same_Text"); semanticBase.Save(semanticBasePath, false);
var semanticOverride = WdbcFile.Load(animationPath); semanticOverride.SetDisplayValue(1, nameColumn, "Crucible_Padding_Text"); semanticOverride.SetDisplayValue(0, nameColumn, "Crucible_Same_Text"); semanticOverride.Save(semanticOverridePath, false);
if (WdbcFile.Load(semanticBasePath).GetRaw(0, nameColumn) == WdbcFile.Load(semanticOverridePath).GetRaw(0, nameColumn))
    throw new InvalidOperationException("Semantic comparison fixture did not create distinct string offsets.");
var semanticDetail = DbcLayerComparer.CompareFiles(semanticBasePath, semanticOverridePath, animationColumns);
if (semanticDetail.ModifiedCells != 1) throw new InvalidOperationException("Layer comparison treated equal decoded strings at different offsets as changes.");
var stringDifferences = DbcPromotionService.GetDifferences(semanticBasePath, semanticOverridePath, animationColumns);
var changedString = stringDifferences.Single(difference => difference.Id == semanticOverride.GetRaw(1, idColumn) && difference.ColumnIndex == nameColumn.Index);
var stringManifest = new DbcPromotionManifest(1, "AnimationData.semantic-base", idColumn.Name, [new(changedString.Id, [nameColumn.Name])]);
// Manifest table names deliberately follow the selected base filename, allowing arbitrary working-copy names.
var promotedAnimationPath = Path.Combine(layerRoot, "AnimationData.promoted.dbc");
DbcPromotionService.Apply(semanticBasePath, semanticOverridePath, promotedAnimationPath, animationColumns, stringManifest);
var promotedAnimation = WdbcFile.Load(promotedAnimationPath);
if (promotedAnimation.GetString(promotedAnimation.GetRaw(1, nameColumn)) != "Crucible_Padding_Text")
    throw new InvalidOperationException("Promoted string was not re-interned into the destination table.");

var stagingRoot = Path.Combine(layerRoot, "my-staging-folder"); Directory.CreateDirectory(Path.Combine(stagingRoot, "Interface", "FrameXML"));
File.WriteAllText(Path.Combine(stagingRoot, "Interface", "FrameXML", "Test.lua"), "-- test");
var stagedEntry = PatchInputMapper.Map([stagingRoot]).Single();
if (stagedEntry.ArchivePath != "Interface\\FrameXML\\Test.lua" || PatchInputMapper.AssessArchivePath(stagedEntry.ArchivePath).HasWarning)
    throw new InvalidOperationException("Staging folder archive-root mapping failed.");
var glueXmlSource = Path.Combine(stagingRoot, "Interface", "GlueXML", "CharacterCreate.lua"); Directory.CreateDirectory(Path.GetDirectoryName(glueXmlSource)!); File.WriteAllText(glueXmlSource, "-- protected test");
var glueXmlEntry = new PatchEntry(glueXmlSource, "Interface\\GlueXML\\CharacterCreate.lua");
if (!PatchInputMapper.AssessArchivePath(glueXmlEntry.ArchivePath).HasWarning || PatchManifestService.GetCompatibilityIssues([glueXmlEntry]).Single().Code != "ProtectedGlueXmlUnbound")
    throw new InvalidOperationException("Protected GlueXML compatibility warning is missing.");
var compatibleExecutable = Path.Combine(layerRoot, "Wow.exe"); File.WriteAllText(compatibleExecutable, "test executable fixture");
var compatibleHash = PatchManifestService.ComputeExecutableSha256(compatibleExecutable);
if (compatibleHash.Length != 64 || PatchManifestService.GetCompatibilityIssues([glueXmlEntry], compatibleHash).Single().Code != "ProtectedGlueXmlBound")
    throw new InvalidOperationException("Protected GlueXML executable binding failed.");
var glueManifestPath = Path.Combine(layerRoot, "glue.crucible-patch.json");
PatchManifestService.Save(glueManifestPath, "Protected UI test", "patch-G.mpq", [glueXmlEntry], compatibleHash);
if (PatchManifestService.Load(glueManifestPath).RequiredClientExecutableSha256 != compatibleHash)
    throw new InvalidOperationException("Client executable hash did not survive manifest save/load.");

var manifestPath = Path.Combine(layerRoot, "classless.crucible-patch.json");
var manifestEntries = PatchInputMapper.Map([Path.Combine(overrideLayer, "SpellCastTimes.dbc")]);
var manifestPolicy = new PatchManifestPolicy(["DBFilesClient\\*.dbc"], ["**\\*.m2", "**\\*.skin"], 1);
if (!PatchManifestService.ValidateEntries(manifestEntries, manifestPolicy).Passed || PatchManifestService.ValidateEntries(manifestEntries, manifestPolicy with { ExpectedEntryCount = 2 }).Passed)
    throw new InvalidOperationException("Manifest allowed-glob or exact-count validation failed.");
var forbiddenPolicyEntry = new PatchEntry(manifestEntries[0].SourcePath, "Character\\Test.m2");
var rootForbiddenPolicyEntry = forbiddenPolicyEntry with { ArchivePath = "Test.m2" };
if (PatchManifestService.ValidateEntries([forbiddenPolicyEntry], manifestPolicy with { AllowedGlobs = ["**"] }).Passed || PatchManifestService.ValidateEntries([rootForbiddenPolicyEntry], manifestPolicy with { AllowedGlobs = ["**"] }).Passed)
    throw new InvalidOperationException("Manifest forbidden-glob validation failed.");
PatchManifestService.Save(manifestPath, "Classless test", "patch-X.mpq", manifestEntries, policy: manifestPolicy);
if (PatchManifestService.Load(manifestPath).Policy?.ExpectedEntryCount != 1) throw new InvalidOperationException("Manifest policy did not survive save/load.");
var builtDirectory = Path.Combine(layerRoot, "built"); Directory.CreateDirectory(builtDirectory); PatchManifestService.Build(manifestPath, builtDirectory);
if (!patchService.Contains(Path.Combine(builtDirectory, "patch-X.mpq"), "DBFilesClient\\SpellCastTimes.dbc")) throw new InvalidOperationException("Manifest-driven patch build failed.");
var builtPatch = Path.Combine(builtDirectory, "patch-X.mpq"); var loadedManifest = PatchManifestService.Load(manifestPath);
if (!PatchManifestService.Validate(loadedManifest, builtPatch).Passed) throw new InvalidOperationException("Built MPQ did not validate against its manifest.");
patchService.Update(builtPatch, PatchInputMapper.Map([animationPath]));
File.AppendAllText(Path.Combine(overrideLayer, "SpellCastTimes.dbc"), "size mismatch");
var mismatchedArchive = PatchManifestService.Validate(loadedManifest, builtPatch);
if (mismatchedArchive.Passed || !mismatchedArchive.Errors.Any(error => error.Code == "UnexpectedArchiveEntry") || !mismatchedArchive.Errors.Any(error => error.Code == "SizeMismatch"))
    throw new InvalidOperationException("Existing MPQ comparison did not report unexpected/size-mismatched content.");
var validationParent = Path.Combine(layerRoot, "validation-parent"); var validationChild = Path.Combine(validationParent, "DBFilesClient"); Directory.CreateDirectory(validationChild); File.Copy(animationPath, Path.Combine(validationChild, "AnimationData.dbc"));
try
{
    _ = DbcCorpusValidator.Validate(args[0], validationParent, verifyRoundTrip: false);
    throw new InvalidOperationException("Zero-file top-directory DBC validation succeeded unexpectedly.");
}
catch (InvalidDataException ex) when (ex.Message.Contains("No DBC files", StringComparison.Ordinal)) { }
var recursiveValidation = DbcCorpusValidator.Validate(args[0], validationParent, verifyRoundTrip: false, recursive: true);
if (recursiveValidation.Count != 1 || !Path.GetFileName(recursiveValidation[0].Path).Equals("AnimationData.dbc", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Recursive DBC validation did not discover the nested corpus.");
Directory.Delete(layerRoot, true);

Console.WriteLine($"PASS: loaded {loaded:N0} WDBC files, cloned 100 real spells in {spellBulkCloneMilliseconds:N0} ms, verified persistence, layered comparison, manifest build, and MPQ workflows.");

using WoWCrucible.Core;
using System.Diagnostics;

if (args.Length != 2)
    throw new ArgumentException("Usage: WoWCrucible.Core.Tests <schema.xml> <dbc-directory>");

var deploymentFixture = Path.Combine(Path.GetTempPath(), $"crucible-client-deploy-{Guid.NewGuid():N}");
var deploymentData = Path.Combine(deploymentFixture, "Data"); var deploymentCache = Path.Combine(deploymentFixture, "Cache", "WDB", "enUS");
Directory.CreateDirectory(deploymentData); Directory.CreateDirectory(deploymentCache);
File.WriteAllText(Path.Combine(deploymentCache, "creaturecache.wdb"), "stale");
var patchDeploymentSource = Path.Combine(Path.GetTempPath(), $"patch-test-{Guid.NewGuid():N}.MPQ"); File.WriteAllText(patchDeploymentSource, "new patch");
var deployed = ClientPatchDeploymentService.Install(patchDeploymentSource, deploymentFixture, "patch-Z.MPQ");
if (Directory.Exists(Path.Combine(deploymentFixture, "Cache")) || File.ReadAllText(deployed.InstalledPath) != "new patch" || !deployed.Cache.Existed)
    throw new InvalidOperationException("Client patch deployment did not atomically install the patch and invalidate Cache.");
Directory.CreateDirectory(Path.Combine(deploymentFixture, "Cache"));
if (!ClientPatchDeploymentService.InvalidateCache(deploymentFixture).Existed || Directory.Exists(Path.Combine(deploymentFixture, "Cache")))
    throw new InvalidOperationException("Explicit client cache invalidation failed.");
Directory.Delete(deploymentFixture, true); File.Delete(patchDeploymentSource);

var assetFixture = Path.Combine(Path.GetTempPath(), $"crucible-native-assets-{Guid.NewGuid():N}"); Directory.CreateDirectory(assetFixture);
var wotlkModel = Path.Combine(assetFixture, "fixture.m2"); var wotlkBytes = new byte[0x130];
System.Text.Encoding.ASCII.GetBytes("MD20").CopyTo(wotlkBytes, 0); BitConverter.GetBytes((uint)264).CopyTo(wotlkBytes, 4); BitConverter.GetBytes((uint)3).CopyTo(wotlkBytes, 0x128);
File.WriteAllBytes(wotlkModel, wotlkBytes); File.WriteAllText(Path.Combine(assetFixture, "fixture00.skin"), "skin");
var compatibleModel = NativeAssetConversionService.Inspect(wotlkModel);
if (compatibleModel.Compatibility != AssetCompatibility.AlreadyWotlk335 || compatibleModel.Version != 264 || compatibleModel.Dependencies.Count != 1)
    throw new InvalidOperationException("Native asset inspection did not recognize a Wrath M2 and its skin dependency.");
var geometryModelPath = Path.Combine(assetFixture, "geometry.m2"); var geometryBytes = new byte[0x130 + 3 * 48 + 80];
System.Text.Encoding.ASCII.GetBytes("MD20").CopyTo(geometryBytes, 0); BitConverter.GetBytes((uint)264).CopyTo(geometryBytes, 4); BitConverter.GetBytes((uint)3).CopyTo(geometryBytes, 0x3C); BitConverter.GetBytes((uint)0x130).CopyTo(geometryBytes, 0x40);
var textureDefinitionOffset = 0x130 + 3 * 48; BitConverter.GetBytes((uint)1).CopyTo(geometryBytes, 0x50); BitConverter.GetBytes((uint)textureDefinitionOffset).CopyTo(geometryBytes, 0x54);
var embeddedFixturePath = @"Character\BloodElf\Female\fixture.blp"; var embeddedFixtureBytes = System.Text.Encoding.ASCII.GetBytes(embeddedFixturePath + "\0");
BitConverter.GetBytes((uint)0).CopyTo(geometryBytes, textureDefinitionOffset); BitConverter.GetBytes((uint)3).CopyTo(geometryBytes, textureDefinitionOffset + 4); BitConverter.GetBytes((uint)embeddedFixtureBytes.Length).CopyTo(geometryBytes, textureDefinitionOffset + 8); BitConverter.GetBytes((uint)(textureDefinitionOffset + 16)).CopyTo(geometryBytes, textureDefinitionOffset + 12); embeddedFixtureBytes.CopyTo(geometryBytes, textureDefinitionOffset + 16);
var fixtureVertices = new[] { (X: -1f, Y: 0f, Z: -1f), (X: 1f, Y: 0f, Z: -1f), (X: 0f, Y: 0f, Z: 1f) };
for (var index = 0; index < fixtureVertices.Length; index++)
{
    var offset = 0x130 + index * 48; BitConverter.GetBytes(fixtureVertices[index].X).CopyTo(geometryBytes, offset); BitConverter.GetBytes(fixtureVertices[index].Y).CopyTo(geometryBytes, offset + 4); BitConverter.GetBytes(fixtureVertices[index].Z).CopyTo(geometryBytes, offset + 8); BitConverter.GetBytes(1f).CopyTo(geometryBytes, offset + 28);
}
File.WriteAllBytes(geometryModelPath, geometryBytes);
var geometrySkin = new byte[60]; System.Text.Encoding.ASCII.GetBytes("SKIN").CopyTo(geometrySkin, 0); BitConverter.GetBytes((uint)3).CopyTo(geometrySkin, 4); BitConverter.GetBytes((uint)48).CopyTo(geometrySkin, 8); BitConverter.GetBytes((uint)3).CopyTo(geometrySkin, 12); BitConverter.GetBytes((uint)54).CopyTo(geometrySkin, 16);
for (ushort index = 0; index < 3; index++) { BitConverter.GetBytes(index).CopyTo(geometrySkin, 48 + index * 2); BitConverter.GetBytes(index).CopyTo(geometrySkin, 54 + index * 2); }
File.WriteAllBytes(Path.Combine(assetFixture, "geometry00.skin"), geometrySkin);
var previewGeometry = M2PreviewGeometryService.Load(geometryModelPath);
if (previewGeometry.Vertices.Count != 3 || previewGeometry.TriangleIndices.Count != 3 || previewGeometry.Minimum.X != -1f || previewGeometry.Maximum.Z != 1f || previewGeometry.TextureSlots.Count != 1 || previewGeometry.TextureSlots[0].EmbeddedPath != embeddedFixturePath || previewGeometry.TextureSlots[0].Flags != 3)
    throw new InvalidOperationException("Native M2/SKIN preview geometry parsing failed.");
var modernModel = Path.Combine(assetFixture, "modern.m2");
using (var stream = File.Create(modernModel)) using (var writer = new BinaryWriter(stream))
{
    writer.Write(System.Text.Encoding.ASCII.GetBytes("MD21")); writer.Write((uint)8); writer.Write(System.Text.Encoding.ASCII.GetBytes("MD20")); writer.Write((uint)274);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("TXID")); writer.Write((uint)8); writer.Write((uint)123); writer.Write((uint)456);
}
var modernInspection = NativeAssetConversionService.Inspect(modernModel);
if (modernInspection.Compatibility != AssetCompatibility.RequiresNativeConversion || modernInspection.Chunks.Count != 2 || modernInspection.Version != 274 || !modernInspection.Findings.Any(finding => finding.Contains("FileDataID")))
    throw new InvalidOperationException("Native asset inspection did not classify a chunked modern M2 or report FileDataID dependencies.");
var wmoFixture = Path.Combine(assetFixture, "fixture.wmo");
using (var stream = File.Create(wmoFixture)) using (var writer = new BinaryWriter(stream))
{
    writer.Write(System.Text.Encoding.ASCII.GetBytes("REVM")); writer.Write((uint)4); writer.Write((uint)17);
}
var wmoInspection = NativeAssetConversionService.Inspect(wmoFixture);
if (wmoInspection.Version != 17 || wmoInspection.Chunks.Single().Id != "MVER")
    throw new InvalidOperationException("Native WMO inspection did not normalize reversed on-disk chunk identifiers.");
var conversionWorkspacePath = Path.Combine(Path.GetTempPath(), $"crucible-native-workspace-{Guid.NewGuid():N}");
var nativeWorkspace = NativeAssetConversionService.CreateWorkspace([wotlkModel, modernModel], conversionWorkspacePath);
if (nativeWorkspace.CompatibleAssets != 1 || nativeWorkspace.ConversionRequired != 1 || !File.Exists(Path.Combine(conversionWorkspacePath, "conversion-report.json")) ||
    Directory.EnumerateFiles(Path.Combine(conversionWorkspacePath, "source"), "fixture.m2", SearchOption.AllDirectories).Count() != 1)
    throw new InvalidOperationException("Native conversion workspace did not preserve immutable hashed inputs and report compatibility.");
var comparisonRoot = Path.Combine(assetFixture, "comparison-library");
var oldLegacy = Path.Combine(comparisonRoot, "Archives", "patch-old-a1", "Content", "Character", "BloodElf", "Female");
var newLegacy = Path.Combine(comparisonRoot, "Archives", "patch-new-b2", "Content", "Character", "BloodElf", "Female"); Directory.CreateDirectory(oldLegacy); Directory.CreateDirectory(newLegacy);
File.WriteAllText(Path.Combine(oldLegacy, "old-expansion-name.png"), "old"); File.WriteAllText(Path.Combine(newLegacy, "completely-renamed.png"), "new"); File.WriteAllText(Path.Combine(newLegacy, "exact-copy.png"), "old");
File.Copy(geometryModelPath, Path.Combine(oldLegacy, "geometry.m2")); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(oldLegacy, "geometry00.skin"));
File.WriteAllText(Path.Combine(oldLegacy, "fixture.blp"), "embedded texture");
var looseMatching = Path.Combine(comparisonRoot, "Loose", "Content", "loose-source", "Character", "BloodElf", "Female"); var looseOnly = Path.Combine(comparisonRoot, "Loose", "Content", "ExtendedSkins", "Character", "Human", "Female");
Directory.CreateDirectory(looseMatching); Directory.CreateDirectory(looseOnly); File.WriteAllText(Path.Combine(looseMatching, "loose-variant.png"), "loose"); File.WriteAllText(Path.Combine(looseOnly, "human-only.png"), "loose only");
var looseModernRace = Path.Combine(comparisonRoot, "Loose", "Content", "WOW Mods", "Textures", "Dracthyr"); var looseMurlocVariant = Path.Combine(comparisonRoot, "Loose", "Content", "WOW Mods", "Textures", "Creature", "Murloc", "1. Oil - Low"); var looseOutfit = Path.Combine(comparisonRoot, "Loose", "Content", "WOW Mods", "Outfits & Armor"); var looseWrappedPatch = Path.Combine(comparisonRoot, "Loose", "Content", "patchzer", "Patch-U_(1.7.1)", "Creature", "DoomGuard");
Directory.CreateDirectory(looseModernRace); Directory.CreateDirectory(looseMurlocVariant); Directory.CreateDirectory(looseOutfit); Directory.CreateDirectory(looseWrappedPatch);
File.WriteAllText(Path.Combine(looseModernRace, "dracthyrfemale_skin.png"), "race"); File.WriteAllText(Path.Combine(looseMurlocVariant, "murloc.png"), "variant"); File.WriteAllText(Path.Combine(looseOutfit, "armor_fixture_lu_u.png"), "outfit"); File.WriteAllText(Path.Combine(looseWrappedPatch, "doomguard.png"), "creature");
File.WriteAllText(Path.Combine(comparisonRoot, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(comparisonRoot, comparisonRoot, 1, DateTimeOffset.UtcNow, 0, [])));
var layoutDryRun = BulkAssetLibraryService.MigrateToContentFirstLayout(comparisonRoot, false); var layoutApplied = BulkAssetLibraryService.MigrateToContentFirstLayout(comparisonRoot, true);
if (layoutDryRun.Files != 6 || layoutDryRun.Conflicts != 0 || layoutApplied.MovedFiles != 6 || BulkAssetLibraryService.MigrateToContentFirstLayout(comparisonRoot, false).SourceFolders != 0)
    throw new InvalidOperationException("Content-first layout migration was destructive, conflicting, or not resumable.");
var consolidationDryRun = BulkAssetLibraryService.ConsolidateLooseLayout(comparisonRoot, false); var consolidationApplied = BulkAssetLibraryService.ConsolidateLooseLayout(comparisonRoot, true);
if (consolidationDryRun.Files != 6 || consolidationDryRun.MovedFiles != 6 || consolidationDryRun.Conflicts != 0 || !consolidationApplied.Applied || consolidationApplied.MovedFiles != 6 || Directory.Exists(Path.Combine(comparisonRoot, "Loose")) || !File.Exists(consolidationApplied.JournalPath) ||
    !File.Exists(Path.Combine(comparisonRoot, "Archives", "Content", "Character", "Dracthyr", "Female", "WOW Mods", "dracthyrfemale_skin.png")) ||
    !File.Exists(Path.Combine(comparisonRoot, "Archives", "Content", "Creature", "Murloc", "WOW Mods - Murloc - 1. Oil - Low", "murloc.png")) ||
    !File.Exists(Path.Combine(comparisonRoot, "Archives", "Content", "Item", "TextureComponents", "LegUpperTexture", "WOW Mods", "armor_fixture_lu_u.png")) ||
    !File.Exists(Path.Combine(comparisonRoot, "Archives", "Content", "Creature", "DoomGuard", "patchzer - Patch-U_(1.7.1)", "doomguard.png")))
    throw new InvalidOperationException("Loose content consolidation did not preview, journal, and apply a content-first move safely.");
var comparisonIndex = AssetComparisonService.BuildIndex(comparisonRoot); var comparisonEntries = AssetComparisonService.GetDirectoryPngs(comparisonIndex, @"Character\BloodElf\Female");
var matchingDirectory = comparisonIndex.Directories.Single(directory => directory.LogicalPath == @"Character\BloodElf\Female");
var looseOnlyEntries = AssetComparisonService.GetDirectoryPngs(comparisonIndex, @"Character\Human\Female");
var exactDuplicates = AssetComparisonService.FindExactDuplicates(comparisonEntries);
var comparisonModels = AssetComparisonService.GetDirectoryModels(comparisonIndex, @"Character\BloodElf\Female");
var ancestorModels = AssetComparisonService.GetRelevantModels(comparisonIndex, @"Character\BloodElf\Female\Hair");
if (comparisonIndex.TotalPngFiles != 9 || matchingDirectory.ProvenanceSources != 3 || comparisonEntries.Count != 4 || comparisonEntries.Select(entry => entry.FileName).Distinct().Count() != 4 ||
    looseOnlyEntries.Count != 1 || looseOnlyEntries[0].Provenance != "ExtendedSkins" || exactDuplicates.Count != 1 || exactDuplicates[0].Entries.Count != 2 || exactDuplicates[0].RecoverableBytes != 3 || comparisonModels.Count != 1 || comparisonModels[0].Compatibility != AssetModelCompatibility.Ready || ancestorModels.DiscoveryScope != @"Character\BloodElf\Female" || ancestorModels.Models.Count != 1)
    throw new InvalidOperationException("Directory-first visual comparison did not merge matching provenance sources after Loose consolidation.");
var definitivePath = DefinitiveAssetProjectService.DefaultPath(comparisonRoot); var definitive = DefinitiveAssetProjectService.LoadOrCreate(definitivePath, comparisonRoot);
definitive = DefinitiveAssetProjectService.RecordTexture(definitivePath, definitive, comparisonEntries[0], AssetDecision.Keeper, "Race", "fixture keeper");
var dependencyGraph = AssetDependencyGraphService.AnalyzeModel(comparisonIndex, comparisonModels[0]);
if (dependencyGraph.Blocking.Count != 0 || !dependencyGraph.Resolved.Any(dependency => dependency.Kind == "embedded-texture" && dependency.ClientPath == embeddedFixturePath))
    throw new InvalidOperationException("Model dependency closure did not resolve an embedded texture from the same provenance layer.");
definitive = DefinitiveAssetProjectService.RecordModel(definitivePath, definitive, comparisonIndex, comparisonModels[0], AssetDecision.Keeper, "Race", "fixture model");
var definitiveStage = DefinitiveAssetProjectService.StageKeepers(definitivePath, definitive, Path.Combine(assetFixture, "definitive-stage"));
if (definitive.Entries.Count != 4 || definitiveStage.Files != 4 || !File.Exists(definitiveStage.ManifestPath) || !PatchManifestService.Validate(PatchManifestService.Load(definitiveStage.ManifestPath)).Passed)
    throw new InvalidOperationException("The persistent Definitive Set did not record, hash, stage, and manifest a keeper.");
try { _ = AssetComparisonService.GetDirectoryPngs(comparisonIndex, ".."); throw new InvalidOperationException("Asset comparison accepted a path outside its content root."); }
catch (InvalidOperationException exception) when (exception.Message.Contains("escaped")) { }
var extractedImport = Path.Combine(assetFixture, "manual-extraction"); Directory.CreateDirectory(Path.Combine(extractedImport, "Interface", "FrameXML"));
File.WriteAllText(Path.Combine(extractedImport, "Interface", "FrameXML", "fixture.lua"), "fixture");
var imported = BulkAssetLibraryService.ImportExtractedArchiveAsync(extractedImport, comparisonRoot, "manual-patch", wotlkModel, 1).GetAwaiter().GetResult();
var resumedImport = BulkAssetLibraryService.ImportExtractedArchiveAsync(extractedImport, comparisonRoot, "manual-patch", wotlkModel, 1).GetAwaiter().GetResult();
if (imported.ImportedFiles != 1 || resumedImport.ImportedFiles != 0 || !File.Exists(Path.Combine(comparisonRoot, "Archives", "Content", "Interface", "FrameXML", "manual-patch", "fixture.lua")))
    throw new InvalidOperationException("Extracted archive import did not preserve provenance or resume with an exact byte comparison.");
var conflictLibrary = Path.Combine(assetFixture, "consolidation-conflict"); var conflictSource = Path.Combine(conflictLibrary, "Loose", "Content", "conflict-source", "Character", "Human", "same.png"); var conflictDestination = Path.Combine(conflictLibrary, "Archives", "Content", "Character", "Human", "conflict-source", "same.png");
Directory.CreateDirectory(Path.GetDirectoryName(conflictSource)!); Directory.CreateDirectory(Path.GetDirectoryName(conflictDestination)!); File.WriteAllText(conflictSource, "legacy"); File.WriteAllText(conflictDestination, "different");
File.WriteAllText(Path.Combine(conflictLibrary, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(conflictLibrary, conflictLibrary, 1, DateTimeOffset.UtcNow, 0, [])));
var blockedConsolidation = BulkAssetLibraryService.ConsolidateLooseLayout(conflictLibrary, true);
if (blockedConsolidation.Applied || blockedConsolidation.Conflicts != 1 || File.ReadAllText(conflictSource) != "legacy" || File.ReadAllText(conflictDestination) != "different")
    throw new InvalidOperationException("Loose consolidation did not block a non-identical destination without changing either file.");

var exactLibrary = Path.Combine(assetFixture, "consolidation-exact"); var exactSource = Path.Combine(exactLibrary, "Loose", "Content", "same-source", "Character", "Human", "same.png"); var exactDestination = Path.Combine(exactLibrary, "Archives", "Content", "Character", "Human", "same-source", "same.png");
Directory.CreateDirectory(Path.GetDirectoryName(exactSource)!); Directory.CreateDirectory(Path.GetDirectoryName(exactDestination)!); File.WriteAllText(exactSource, "identical"); File.WriteAllText(exactDestination, "identical");
File.WriteAllText(Path.Combine(exactLibrary, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(exactLibrary, exactLibrary, 1, DateTimeOffset.UtcNow, 0, [])));
var exactConsolidation = BulkAssetLibraryService.ConsolidateLooseLayout(exactLibrary, true);
if (!exactConsolidation.Applied || exactConsolidation.ExactDuplicates != 1 || File.Exists(exactSource) || File.ReadAllText(exactDestination) != "identical")
    throw new InvalidOperationException("Loose consolidation removed a source without a verified byte-identical destination.");

var relocationLibrary = Path.Combine(assetFixture, "relocation-preflight"); var relocationLegacy = Path.Combine(relocationLibrary, "Archives", "legacy-source", "Content", "Character", "Human");
var relocationSafeSource = Path.Combine(relocationLegacy, "safe.png"); var relocationConflictSource = Path.Combine(relocationLegacy, "conflict.png"); var relocationConflictDestination = Path.Combine(relocationLibrary, "Archives", "Content", "Character", "Human", "legacy-source", "conflict.png");
Directory.CreateDirectory(relocationLegacy); Directory.CreateDirectory(Path.GetDirectoryName(relocationConflictDestination)!); File.WriteAllText(relocationSafeSource, "safe"); File.WriteAllText(relocationConflictSource, "source"); File.WriteAllText(relocationConflictDestination, "destination");
File.WriteAllText(Path.Combine(relocationLibrary, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(relocationLibrary, relocationLibrary, 1, DateTimeOffset.UtcNow, 0, [])));
var blockedRelocation = BulkAssetLibraryService.MigrateToContentFirstLayout(relocationLibrary, true);
if (blockedRelocation.Applied || blockedRelocation.MovedFiles != 0 || blockedRelocation.Conflicts != 1 || !File.Exists(relocationSafeSource) || File.Exists(Path.Combine(relocationLibrary, "Archives", "Content", "Character", "Human", "legacy-source", "safe.png")))
    throw new InvalidOperationException("Content relocation moved files before its complete conflict preflight passed.");

var directSourceRoot = Path.Combine(assetFixture, "direct-source"); var directSource = Path.Combine(directSourceRoot, "Character", "Human", "Male", "collision.blp"); var directLibrary = Path.Combine(assetFixture, "direct-library");
Directory.CreateDirectory(Path.GetDirectoryName(directSource)!); File.WriteAllText(directSource, "ABCD"); BulkAssetLibraryService.CreatePlan(directSourceRoot, directLibrary, long.MaxValue);
var directProvenance = Path.GetFileName(directSourceRoot); var directDestination = Path.Combine(directLibrary, "Archives", "Content", "Character", "Human", "Male", directProvenance, "collision.blp");
Directory.CreateDirectory(Path.GetDirectoryName(directDestination)!); File.WriteAllText(directDestination, "WXYZ");
var fixedStamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(directSource, fixedStamp); File.SetLastWriteTimeUtc(directDestination, fixedStamp);
var directHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(directSource))).ToLowerInvariant()[..24]; var directVariant = Path.Combine(Path.GetDirectoryName(directDestination)!, $"collision.variant-{directHash}.blp");
File.WriteAllText(Path.ChangeExtension(directDestination, ".png"), "preview"); File.WriteAllText(Path.ChangeExtension(directVariant, ".png"), "variant preview");
var firstDirectRun = BulkAssetLibraryService.RunAsync(directLibrary, wotlkModel, 1).GetAwaiter().GetResult(); var resumedDirectRun = BulkAssetLibraryService.RunAsync(directLibrary, wotlkModel, 1).GetAwaiter().GetResult();
if (firstDirectRun.CopiedLooseBlps != 1 || resumedDirectRun.CopiedLooseBlps != 0 || File.ReadAllText(directDestination) != "WXYZ" || File.ReadAllText(directVariant) != "ABCD" || Directory.EnumerateFiles(Path.GetDirectoryName(directDestination)!, "collision.variant-*.blp").Count() != 1)
    throw new InvalidOperationException("Direct loose intake trusted timestamps, lost a differing file, produced an unstable variant, or discarded root provenance.");
var directCatalogText = File.ReadAllText(firstDirectRun.CatalogPath);
if (directCatalogText.Contains("asset-library-plan", StringComparison.OrdinalIgnoreCase) || directCatalogText.Contains("asset-library-checkpoint", StringComparison.OrdinalIgnoreCase) || directCatalogText.Contains(".asset-library-operation.lock", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The asset catalog included library control files instead of only processed asset roots.");

var provenanceLibrary = Path.Combine(assetFixture, "provenance-collision"); var sharedProvenancePrefix = new string('p', 60); var rawProvenanceA = sharedProvenancePrefix + "-first"; var rawProvenanceB = sharedProvenancePrefix + "-second";
var provenanceSourceA = Path.Combine(provenanceLibrary, "Loose", "Content", rawProvenanceA, "Character", "Human", "same.png"); var provenanceSourceB = Path.Combine(provenanceLibrary, "Loose", "Content", rawProvenanceB, "Character", "Human", "same.png");
Directory.CreateDirectory(Path.GetDirectoryName(provenanceSourceA)!); Directory.CreateDirectory(Path.GetDirectoryName(provenanceSourceB)!); File.WriteAllText(provenanceSourceA, "first"); File.WriteAllText(provenanceSourceB, "second");
File.WriteAllText(Path.Combine(provenanceLibrary, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(provenanceLibrary, provenanceLibrary, 1, DateTimeOffset.UtcNow, 0, [])));
var provenanceConsolidation = BulkAssetLibraryService.ConsolidateLooseLayout(provenanceLibrary, true); var provenanceDestinations = Directory.EnumerateDirectories(Path.Combine(provenanceLibrary, "Archives", "Content", "Character", "Human")).ToArray();
if (!provenanceConsolidation.Applied || provenanceConsolidation.Conflicts != 0 || provenanceDestinations.Length != 2 || provenanceDestinations.Select(path => File.ReadAllText(Path.Combine(path, "same.png"))).Order().SequenceEqual(new[] { "first", "second" }) == false)
    throw new InvalidOperationException("Sanitized or truncated provenance labels collapsed two distinct source packages.");

var recoverableLibrary = Path.Combine(assetFixture, "recoverable-catalog"); var recoverableSource = Path.Combine(recoverableLibrary, "Loose", "Content", "recovery-source", "Character", "Human", "recovery.png");
Directory.CreateDirectory(Path.GetDirectoryName(recoverableSource)!); File.WriteAllText(recoverableSource, "recovery"); File.WriteAllText(Path.Combine(recoverableLibrary, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(recoverableLibrary, recoverableLibrary, 1, DateTimeOffset.UtcNow, 0, [])));
var catalogBlocker = Path.Combine(recoverableLibrary, "asset-catalog.csv"); Directory.CreateDirectory(catalogBlocker); var recoverableConsolidation = BulkAssetLibraryService.ConsolidateLooseLayout(recoverableLibrary, true);
var failedJournal = System.Text.Json.JsonSerializer.Deserialize<LooseAssetConsolidationJournal>(File.ReadAllText(recoverableConsolidation.JournalPath));
if (!recoverableConsolidation.Applied || recoverableConsolidation.CatalogRebuildError is null || failedJournal?.FilesCommittedUtc is null || failedJournal.CatalogCompletedUtc is not null || File.Exists(recoverableSource) || !File.Exists(Path.Combine(recoverableLibrary, "Archives", "Content", "Character", "Human", "recovery-source", "recovery.png")))
    throw new InvalidOperationException("A post-commit catalog failure obscured or rolled back an already-safe Loose consolidation.");
Directory.Delete(catalogBlocker); var rebuiltCatalog = BulkAssetLibraryService.RebuildCatalog(recoverableLibrary); var recoveredJournal = System.Text.Json.JsonSerializer.Deserialize<LooseAssetConsolidationJournal>(File.ReadAllText(recoverableConsolidation.JournalPath));
if (!File.Exists(rebuiltCatalog) || recoveredJournal?.CatalogCompletedUtc is null || recoveredJournal.CatalogRebuildError is not null || File.ReadAllText(rebuiltCatalog).Contains("Reports", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The catalog could not be rebuilt independently or its journal did not record recovery.");
Directory.Delete(assetFixture, true); Directory.Delete(conversionWorkspacePath, true);

var targetProfiles = TargetProfileCatalog.Load(Path.Combine(Path.GetTempPath(), $"crucible-profiles-{Guid.NewGuid():N}"), Path.Combine(Path.GetTempPath(), $"crucible-app-profiles-{Guid.NewGuid():N}"));
if (targetProfiles.Count != 4 || TargetProfileCatalog.Find(targetProfiles, null).ClientBuild != 12340 ||
    targetProfiles.Single(profile => profile.ClientBuild == 15595).SupportTier != TargetSupportTier.Experimental)
    throw new InvalidOperationException("Built-in target profiles are incomplete or the verified default changed.");
var contentProjectRoot = Path.Combine(Path.GetTempPath(), $"crucible-content-project-{Guid.NewGuid():N}"); CrucibleContentProjectService.Create(contentProjectRoot, "Fixture project");
var firstIds = CrucibleContentProjectService.ReserveIds(contentProjectRoot, ContentIdDomain.CreatureDisplayInfo, 3, 100, [100u, 102u], "fixture displays").Reservation.Values;
var secondIds = CrucibleContentProjectService.ReserveIds(contentProjectRoot, ContentIdDomain.CreatureDisplayInfo, 2, 100, [100u, 102u], "more displays").Reservation.Values;
if (!firstIds.SequenceEqual([101u, 103u, 104u]) || !secondIds.SequenceEqual([105u, 106u]) || CrucibleContentProjectService.LoadRegistry(contentProjectRoot).Reservations.Count != 2 || !Directory.Exists(Path.Combine(contentProjectRoot, "Staging")))
    throw new InvalidOperationException("Portable content-project ID reservations collided with occupied or previously reserved IDs.");
Directory.Delete(contentProjectRoot, true);
var customProfileDirectory = Path.Combine(Path.GetTempPath(), $"crucible-custom-profile-{Guid.NewGuid():N}");
TargetProfileCatalog.SaveTemplate(Path.Combine(customProfileDirectory, "custom.json"), new("custom-9999", "Custom Test Build", "Test", 9999, "custom.xml", ClientTableFormat.Wdbc, ArchiveFormat.Mpq, TargetSupportTier.Experimental, "Fixture"));
var profilesWithCustom = TargetProfileCatalog.Load(customProfileDirectory, Path.Combine(Path.GetTempPath(), $"crucible-empty-{Guid.NewGuid():N}"));
if (profilesWithCustom.Count != 5 || profilesWithCustom.Single(profile => profile.Id == "custom-9999").ClientBuild != 9999)
    throw new InvalidOperationException("External target profile loading failed.");
Directory.Delete(customProfileDirectory, true);

var serverFixture = Path.Combine(Path.GetTempPath(), $"crucible-server-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(serverFixture, "etc")); Directory.CreateDirectory(Path.Combine(serverFixture, "data", "dbc"));
File.WriteAllText(Path.Combine(serverFixture, "etc", "worldserver.conf"), """
    # Parser must ignore comments and whitespace.
    WorldDatabaseInfo = "localhost;3307;fixture_user;fixture_password;fixture_world"
    DataDir = "./data"
    """);
var detectedServer = ServerWorkspaceDetector.DetectLocal(serverFixture);
if (detectedServer.WorldDatabase.Host != "127.0.0.1" || detectedServer.WorldDatabase.Port != 3307 || detectedServer.WorldDatabase.Password != "fixture_password" ||
    detectedServer.WorldDatabase.Database != "fixture_world" || !detectedServer.DbcPath.EndsWith(Path.Combine("data", "dbc")))
    throw new InvalidOperationException("Native server workspace detection failed.");
Directory.CreateDirectory(Path.Combine(serverFixture, "configured-data", "dbc"));
File.WriteAllText(Path.Combine(serverFixture, "etc", "worldserver.conf"), """
    WorldDatabaseInfo = "localhost;3307;fixture_user;fixture_password;fixture_world"
    DataDir = "./configured-data"
    """);
var explicitlyConfiguredServer = ServerWorkspaceDetector.DetectLocal(serverFixture);
if (!explicitlyConfiguredServer.DbcPath.EndsWith(Path.Combine("configured-data", "dbc")))
    throw new InvalidOperationException("An incidental root data\\dbc incorrectly overrode the explicit DataDir setting.");
File.Delete(Path.Combine(serverFixture, "etc", "worldserver.conf")); File.WriteAllText(Path.Combine(serverFixture, "etc", "worldserver.conf.dist"), "WorldDatabaseInfo = \"bad;3306;bad;bad;bad\"");
try { _ = ServerWorkspaceDetector.DetectLocal(serverFixture); throw new InvalidOperationException("A .conf.dist template was accepted as a live server configuration."); }
catch (FileNotFoundException) { }
Directory.Delete(serverFixture, true);

var azerothItemTable = new DatabaseTableCapability("item_template", ItemColumns("entry", "class", "subclass", "name", "displayid", "Quality", "InventoryType", "ItemLevel", "RequiredLevel", "BuyPrice", "SellPrice", "bonding", "Flags", "armor", "dmg_min1", "dmg_max1", "delay", "MaxDurability", "description", "stat_type1", "stat_value1", "spellid_1", "spelltrigger_1", "spellcooldown_1"));
var trinityItemTable = new DatabaseTableCapability("item_template", ItemColumns("entry", "class", "subclass", "name", "displayid", "Quality", "InventoryType", "ItemLevel", "StatsCount", "stat_type1", "stat_value1", "stat_type2", "stat_value2", "spellid_1", "spelltrigger_1", "spellcharges_1", "spellppmRate_1", "spellcooldown_1", "spellcategory_1", "spellcategorycooldown_1"));
var itemDraft = new ItemDraft(900001, "Crucible's Blade", 2, 8, 123, 4, 17, 200, 80, 10000, 2500, 2, 0, 0, 120, 180, 2800, 120, "Adapter test", [new(4, 50), new(7, 75)], [new(12345, 1, 0, 0, -1, 0, -1)]);
var azerothPlan = ItemTemplateAdapter.CreatePlan(itemDraft, azerothItemTable);
var trinityPlan = ItemTemplateAdapter.CreatePlan(itemDraft, trinityItemTable);
var portablePlan = ItemTemplateAdapter.CreatePlan(itemDraft, ItemTemplateAdapter.CreatePortableTable());
var itemSetPlan = ItemTemplateAdapter.CreatePlan(itemDraft with { ItemSetId = 4321 }, ItemTemplateAdapter.CreatePortableTable());
var gapStatsPlan = ItemTemplateAdapter.CreatePlan(itemDraft with { Stats = [new(0, 0), new(7, 75)] }, trinityItemTable);
if (!azerothPlan.PreviewSql().Contains("Crucible''s Blade") || trinityPlan.Values["StatsCount"] is not 2 || trinityPlan.Values.ContainsKey("MaxDurability") || trinityPlan.Values["spellid_1"] is not 12345 || portablePlan.OmittedFields.Count != 0 || itemSetPlan.Values["itemset"] is not 4321u)
    throw new InvalidOperationException("Capability-aware item mapping or SQL escaping failed.");
if (gapStatsPlan.Values["StatsCount"] is not 1 || gapStatsPlan.Values["stat_type1"] is not 7 || gapStatsPlan.Values["stat_value1"] is not 75 || gapStatsPlan.Values["stat_type2"] is not 0)
    throw new InvalidOperationException("Trinity item stat slots were not compacted before StatsCount was calculated.");

static IReadOnlyList<DatabaseColumnCapability> ItemColumns(params string[] names) => names.Select((name, index) => new DatabaseColumnCapability(name, "int", "int", false, "0", name == "entry" ? "PRI" : "", "", index + 1)).ToArray();

var schema = DbcSchemaCatalog.Load(args[0]);
var skillAbilitySchema = schema.ResolveColumns("SkillLineAbility", 14);
var visualKitSchema = schema.ResolveColumns("SpellVisualKit", 38);
if (skillAbilitySchema.UsedFallback || skillAbilitySchema.Columns[5].Name != "ExcludeRace" || skillAbilitySchema.Columns[13].Name != "CharacterPoints[1]" || visualKitSchema.UsedFallback || visualKitSchema.Columns[37].Name != "Flags")
    throw new InvalidOperationException("Known build-12340 schema corrections were not applied.");
var builtInSchema = DbcSchemaCatalog.CreateBuiltIn12340();
var builtInSpellColumns = builtInSchema.GetColumns("Spell", 234);
if (builtInSpellColumns[136].Name != "Name[enUS]" || builtInSpellColumns[233].Name != "SpellDifficultyID")
    throw new InvalidOperationException("Built-in Spell.dbc schema is incomplete.");
if (builtInSchema.ResolveColumns("NotARealTable", 4).MatchKind != DbcSchemaMatchKind.MissingTableFallback ||
    builtInSchema.ResolveColumns("Spell", 233).MatchKind != DbcSchemaMatchKind.FieldCountMismatchFallback ||
    builtInSchema.ResolveColumns("Spell", 234).MatchKind != DbcSchemaMatchKind.NamedMatch)
    throw new InvalidOperationException("Schema resolution did not expose named/fallback match status.");
if (builtInSchema.ResolveColumns("NotARealTable", 4).KeyStrategy.Kind != DbcRecordKeyKind.NoStableKey)
    throw new InvalidOperationException("Fallback schemas still guess that the first physical value is a stable ID.");
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

var generatedKeyPath = files.First(path => Path.GetFileName(path).Equals("gtRegenMPPerSpt.dbc", StringComparison.OrdinalIgnoreCase));
var generatedSchema = schema.ResolveColumns("gtRegenMPPerSpt", WdbcFile.Load(generatedKeyPath).FieldCount);
if (generatedSchema.KeyStrategy.Kind != DbcRecordKeyKind.VirtualRowIndex || generatedSchema.Columns.Count != 1 || generatedSchema.Columns[0].Name != "Data")
    throw new InvalidOperationException("AutoGenerate schema fields were not modeled as virtual row keys.");
var generatedBasePath = Path.Combine(Path.GetTempPath(), $"crucible-gt-base-{Guid.NewGuid():N}.dbc");
var generatedOverridePath = Path.Combine(Path.GetTempPath(), $"crucible-gt-override-{Guid.NewGuid():N}.dbc");
File.Copy(generatedKeyPath, generatedBasePath);
var generatedOverride = WdbcFile.Load(generatedKeyPath);
var generatedData = generatedSchema.Columns[0];
var originalRow900 = generatedOverride.GetRaw(900, generatedData);
generatedOverride.SetRaw(900, generatedData, originalRow900 + 1);
var appendedVirtualRow = generatedOverride.CloneRow(900);
if (appendedVirtualRow != 1100 || generatedOverride.GetRaw(appendedVirtualRow, generatedData) != originalRow900 + 1)
    throw new InvalidOperationException("Appending a generated-key GT row changed its physical Data value.");
generatedOverride.Save(generatedOverridePath, false);
var generatedComparison = DbcLayerComparer.CompareFiles(generatedBasePath, generatedOverridePath, generatedSchema.Columns, generatedSchema.KeyStrategy);
if (generatedComparison.AddedRows != 1 || generatedComparison.ModifiedRows != 1 || generatedComparison.ModifiedCells != 1)
    throw new InvalidOperationException("Generated-key GT rows were not compared by virtual row index.");
var generatedDifferences = DbcPromotionService.GetDifferences(generatedBasePath, generatedOverridePath, generatedSchema.Columns, generatedSchema.KeyStrategy);
if (!generatedDifferences.Any(difference => difference.Id == 900 && difference.ColumnName == "Data") || !generatedDifferences.Any(difference => difference.Id == 1100 && difference.ColumnIndex == -1))
    throw new InvalidOperationException("Generated-key differences did not report row 900 and appended row 1100 by virtual ID.");
File.Delete(generatedBasePath); File.Delete(generatedOverridePath);

var azerothBindings = ServerTableBindingCatalog.BuiltIn(ServerCoreFamily.AzerothCore);
var manaBinding = azerothBindings.Single(binding => binding.DbcFileName.Equals("gtRegenMPPerSpt.dbc", StringComparison.OrdinalIgnoreCase));
var unusedManaBinding = azerothBindings.Single(binding => binding.DbcFileName.Equals("gtOCTRegenMP.dbc", StringComparison.OrdinalIgnoreCase));
if (manaBinding.Consumption != ServerTableConsumption.SqlOverlayed || manaBinding.SqlTableName != "gtregenmpperspt_dbc" || manaBinding.DescribeRow(900) != "class 10, level 1" || unusedManaBinding.Consumption != ServerTableConsumption.Unused)
    throw new InvalidOperationException("The built-in AzerothCore GT binding profile is incorrect.");
var bindingSourceFixture = Path.Combine(Path.GetTempPath(), $"crucible-dbcstores-{Guid.NewGuid():N}.cpp");
File.WriteAllText(bindingSourceFixture, """
    LOAD_DBC(sGtRegenMPPerSptStore, "gtRegenMPPerSpt.dbc", "gtregenmpperspt_dbc");
    //LOAD_DBC(sGtOCTRegenMPStore, "gtOCTRegenMP.dbc", "gtoctregenmp_dbc"); -- not used
    """);
var sourceBindings = ServerTableBindingCatalog.ParseSource(ServerCoreFamily.AzerothCore, bindingSourceFixture, azerothBindings.ToDictionary(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase));
File.Delete(bindingSourceFixture);
if (sourceBindings.Count != 2 || !sourceBindings.All(binding => binding.SourceBacked) || sourceBindings[0].Consumption != ServerTableConsumption.SqlOverlayed || sourceBindings[1].Consumption != ServerTableConsumption.Unused)
    throw new InvalidOperationException("Source-backed DBCStores binding discovery failed.");

var incidentDbc = WdbcFile.Load(generatedKeyPath);
for (var key = 900; key < 1000; key++) incidentDbc.SetDisplayValue(key, generatedData, 1f + (key - 900) / 100f);
var incidentSql = new Dictionary<uint, IReadOnlyDictionary<string, object?>>();
for (uint key = 0; key < incidentDbc.RowCount; key++)
    incidentSql[key] = new Dictionary<string, object?> { ["Data"] = key is >= 900 and < 1000 ? 0f : incidentDbc.GetDisplayValue((int)key, generatedData) };
var incidentAudit = DbcSqlAuditService.Compare(manaBinding, generatedKeyPath, incidentDbc, generatedSchema, incidentSql);
if (incidentAudit.Rows.Count(row => row.Status == DbcSqlRowStatus.SqlOverridesDbc) != 100 || incidentAudit.Rows.Single(row => row.Key == 900).Dimensions != "class 10, level 1")
    throw new InvalidOperationException("The class-10 mana incident was not diagnosed as exactly 100 SQL overrides.");
var incidentMigration = DbcSqlAuditService.CreateIdempotentMigration(incidentAudit);
if (!incidentMigration.Contains("`gtregenmpperspt_dbc`") || !incidentMigration.Contains("(900,") || !incidentMigration.Contains("ON DUPLICATE KEY UPDATE"))
    throw new InvalidOperationException("The DBC/SQL audit did not produce an idempotent migration preview.");
var aliasAudit = new DbcSqlAuditResult(manaBinding, generatedKeyPath, "ID", [new(1, "row 1", DbcSqlRowStatus.SqlOverridesDbc, new Dictionary<string, object?> { ["TextureVariation[0]"] = "Character\\Test.blp" }, new Dictionary<string, object?>())], new Dictionary<string, string> { ["TextureVariation[0]"] = "TextureVariation_1" });
if (!DbcSqlAuditService.CreateIdempotentMigration(aliasAudit).Contains("`TextureVariation_1`", StringComparison.Ordinal))
    throw new InvalidOperationException("DBC-to-SQL migration did not use the inspected SQL alias for an array field.");

var physicalKeyPath = files.First(path => Path.GetFileName(path).Equals("gtOCTClassCombatRatingScalar.dbc", StringComparison.OrdinalIgnoreCase));
var physicalSchema = schema.ResolveColumns("gtOCTClassCombatRatingScalar", WdbcFile.Load(physicalKeyPath).FieldCount);
if (physicalSchema.KeyStrategy.Kind != DbcRecordKeyKind.PhysicalColumn || physicalSchema.KeyStrategy.ColumnIndex != 0)
    throw new InvalidOperationException("A GT table with a real physical ID stopped using its ID column.");
var physicalDbc = WdbcFile.Load(physicalKeyPath); var physicalDataColumn = physicalSchema.Columns.Single(column => column.Name == "Data"); var physicalIdColumn = physicalSchema.Columns.Single(column => column.IsIndex);
var physicalSql = Enumerable.Range(0, physicalDbc.RowCount).ToDictionary(row => physicalDbc.GetRaw(row, physicalIdColumn), row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Data"] = physicalDbc.GetDisplayValue(row, physicalDataColumn) });
var physicalAudit = DbcSqlAuditService.Compare(azerothBindings.Single(binding => binding.DbcFileName.Equals("gtOCTClassCombatRatingScalar.dbc", StringComparison.OrdinalIgnoreCase)), physicalKeyPath, physicalDbc, physicalSchema, physicalSql);
if (physicalAudit.MismatchCount != 0) throw new InvalidOperationException("Physical-ID GT SQL parity was not compared by its stored ID.");

var overlayCapabilities = new DatabaseCapabilities("fixture", "world", new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { ["gtregenmpperspt_dbc"] = new("gtregenmpperspt_dbc", []) });
if (overlayCapabilities.DbcOverlayTables.Count != 1) throw new InvalidOperationException("DBC SQL overlay tables are not exposed by database capabilities.");

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
var animationResolution = schema.ResolveColumns("AnimationData", animation.FieldCount);
var animationColumns = animationResolution.Columns;
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
if (!listed.Any(entry => entry.IsMetadata) || listed.Count(entry => !entry.IsMetadata) != 2)
    throw new InvalidOperationException("MPQ metadata was not distinguished from payload content.");
var clientFixture = Path.Combine(Path.GetTempPath(), $"crucible-client-{Guid.NewGuid():N}"); var clientData = Path.Combine(clientFixture, "Data"); Directory.CreateDirectory(clientData);
Directory.CreateDirectory(Path.Combine(clientFixture, "WTF")); File.WriteAllText(Path.Combine(clientFixture, "WTF", "Config.wtf"), "SET locale \"enUS\"");
File.Copy(mpqOutput, Path.Combine(clientData, "patch-test.mpq")); var clientIndexDirectory = Path.Combine(clientFixture, "index");
var clientIndexer = new ClientArchiveIndexService(); var firstClientIndex = clientIndexer.Build(clientFixture, clientIndexDirectory, false); var secondClientIndex = clientIndexer.Build(clientFixture, clientIndexDirectory, false);
if (!firstClientIndex.Complete || firstClientIndex.ActiveLocale != "enUS" || firstClientIndex.Archives.Count != 1 || firstClientIndex.Archives[0].Scope != ClientArchiveScope.RootData || firstClientIndex.Archives[0].PayloadFiles != 2 || firstClientIndex.Archives[0].MetadataFiles == 0 || firstClientIndex.Archives[0].AnonymousFiles != 0 || secondClientIndex.Archives[0] != firstClientIndex.Archives[0] || firstClientIndex.LooseFiles?.Single().Scope != ClientLooseFileScope.Configuration)
    throw new InvalidOperationException("Resumable structured client indexing failed.");
if (File.Exists(Path.Combine(clientIndexDirectory, "client-index.partial.json")))
    throw new InvalidOperationException("A completed client index left its partial refresh summary behind.");
var simulatedPartialPath = Path.Combine(clientIndexDirectory, "client-index.partial.json");
File.WriteAllText(simulatedPartialPath, System.Text.Json.JsonSerializer.Serialize(firstClientIndex with { Complete = false, CompletedArchives = 0, Archives = [] }));
if (!ClientArchiveIndexService.Load(clientIndexDirectory).Complete) throw new InvalidOperationException("A partial refresh hid the last complete client index from readers.");
File.Delete(simulatedPartialPath);
var corpusPath = Path.Combine(clientFixture, "known-paths.txt");
if (ClientArchiveIndexService.CreatePathCorpus([clientIndexDirectory], corpusPath) != 2)
    throw new InvalidOperationException("Client path corpus creation failed.");
var indexedExtractRoot = Path.Combine(clientFixture, "indexed-extract");
var indexedExtract = ClientArchiveIndexService.ExtractIndexed(clientIndexDirectory, "Data\\patch-test.mpq", indexedExtractRoot, "SpellCastTimes.dbc");
var resumedExtract = ClientArchiveIndexService.ExtractIndexed(clientIndexDirectory, "Data\\patch-test.mpq", indexedExtractRoot, "SpellCastTimes.dbc");
var globExtract = ClientArchiveIndexService.ExtractIndexed(clientIndexDirectory, "Data\\patch-test.mpq", Path.Combine(indexedExtractRoot, "glob"), "DBFilesClient\\*.dbc");
if (indexedExtract.ExtractedFiles != 1 || resumedExtract.SkippedExistingFiles != 1 || !File.Exists(Path.Combine(indexedExtractRoot, "DBFilesClient", "SpellCastTimes.dbc")))
    throw new InvalidOperationException("Resumable indexed extraction failed.");
if (globExtract.SelectedFiles != 2 || globExtract.ExtractedFiles != 2)
    throw new InvalidOperationException("Indexed extraction path globs were treated as literal text.");
Directory.Delete(clientFixture, true);
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
var castTimesOverride = WdbcFile.Load(castTimesSource); var castResolution = schema.ResolveColumns("SpellCastTimes", castTimesOverride.FieldCount); var castColumns = castResolution.Columns;
castTimesOverride.SetRaw(0, castColumns[1], castTimesOverride.GetRaw(0, castColumns[1]) + 1); castTimesOverride.Save(Path.Combine(overrideLayer, "SpellCastTimes.dbc"), false);
var durationSource = files.First(path => Path.GetFileName(path).Equals("SpellDuration.dbc", StringComparison.OrdinalIgnoreCase)); File.Copy(durationSource, Path.Combine(overrideLayer, "SpellDuration.dbc"));
var layers = DbcLayerComparer.CompareDirectories(baseLayer, overrideLayer);
if (layers.Single(entry => entry.Name == "AnimationData.dbc").Status != DbcLayerStatus.Identical || layers.Single(entry => entry.Name == "SpellCastTimes.dbc").Status != DbcLayerStatus.Overridden || layers.Single(entry => entry.Name == "SpellDuration.dbc").Status != DbcLayerStatus.OverrideOnly)
    throw new InvalidOperationException("Layer classification failed.");
var layerDetail = DbcLayerComparer.CompareFiles(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns, castResolution.KeyStrategy);
if (layerDetail.ModifiedRows != 1 || layerDetail.ModifiedCells != 1) throw new InvalidOperationException("Detailed layered row comparison failed.");

var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try
{
    _ = DbcLayerComparer.CompareFiles(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns, castResolution.KeyStrategy, cancelled.Token);
    throw new InvalidOperationException("Cancelled layered comparison completed unexpectedly.");
}
catch (OperationCanceledException) { }

var promotionDifferences = DbcPromotionService.GetDifferences(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns, castResolution.KeyStrategy);
if (promotionDifferences.Count != 1 || promotionDifferences[0].Id != WdbcFile.Load(castTimesSource).GetRaw(0, castColumns[0]))
    throw new InvalidOperationException("ID-keyed promotion differences were incorrect.");
var promotionOperation = new DbcPromotionOperation(promotionDifferences[0].Id, [promotionDifferences[0].ColumnName]);
var promotionManifestPath = Path.Combine(layerRoot, "cast-times.crucible-promotion.json");
DbcPromotionService.SaveManifest(promotionManifestPath, "SpellCastTimes", castColumns[0].Name, [promotionOperation]);
var promotionManifest = DbcPromotionService.LoadManifest(promotionManifestPath);
var promotedCastTimesPath = Path.Combine(layerRoot, "SpellCastTimes.promoted.dbc");
DbcPromotionService.Apply(Path.Combine(baseLayer, "SpellCastTimes.dbc"), Path.Combine(overrideLayer, "SpellCastTimes.dbc"), promotedCastTimesPath, castColumns, castResolution.KeyStrategy, promotionManifest);
var promotedCastTimes = WdbcFile.Load(promotedCastTimesPath);
if (promotedCastTimes.GetRaw(0, castColumns[1]) != castTimesOverride.GetRaw(0, castColumns[1]))
    throw new InvalidOperationException("Selected field promotion did not persist.");
var additionsOverridePath = Path.Combine(layerRoot, "SpellCastTimes.additions-source.dbc"); var additionsSource = WdbcFile.Load(castTimesSource);
additionsSource.CloneRow(0, castColumns[0]); additionsSource.Save(additionsOverridePath, false);
var additionsManifest = DbcPromotionService.CreateAdditionsManifest(castTimesSource, additionsOverridePath, castColumns, castResolution.KeyStrategy);
var additionsOutputPath = Path.Combine(layerRoot, "SpellCastTimes.additions-output.dbc");
DbcPromotionService.Apply(castTimesSource, additionsOverridePath, additionsOutputPath, castColumns, castResolution.KeyStrategy, additionsManifest);
if (additionsManifest.Operations.Count != 1 || WdbcFile.Load(additionsOutputPath).RowCount != WdbcFile.Load(castTimesSource).RowCount + 1)
    throw new InvalidOperationException("Additive-only DBC promotion did not preserve the base while appending absent IDs.");
var sourceCastId = WdbcFile.Load(castTimesSource).GetRaw(0, castColumns[0]);
var identicalCloneManifest = DbcCloneRemapService.CreateManifest(castTimesSource, castTimesSource, castColumns, castResolution.KeyStrategy, [sourceCastId]);
var identicalCloneOutput = Path.Combine(layerRoot, "SpellCastTimes.identical-dedup.dbc");
DbcCloneRemapService.Apply(castTimesSource, castTimesSource, identicalCloneOutput, castColumns, castResolution.KeyStrategy, identicalCloneManifest);
if (identicalCloneManifest.Entries.Count != 0 || WdbcFile.Load(identicalCloneOutput).RowCount != WdbcFile.Load(castTimesSource).RowCount)
    throw new InvalidOperationException("Clone/remap duplicated an identical same-ID record.");
var equivalentSourceId = additionsManifest.Operations.Single().Id;
var equivalentCloneManifest = DbcCloneRemapService.CreateManifest(castTimesSource, additionsOverridePath, castColumns, castResolution.KeyStrategy, [equivalentSourceId]);
var equivalentCloneOutput = Path.Combine(layerRoot, "SpellCastTimes.equivalent-dedup.dbc");
DbcCloneRemapService.Apply(castTimesSource, additionsOverridePath, equivalentCloneOutput, castColumns, castResolution.KeyStrategy, equivalentCloneManifest);
if (equivalentCloneManifest.Entries.Count != 1 || !equivalentCloneManifest.Entries[0].ReusesExisting || equivalentCloneManifest.Entries[0].TargetId != sourceCastId ||
    WdbcFile.Load(equivalentCloneOutput).RowCount != WdbcFile.Load(castTimesSource).RowCount)
    throw new InvalidOperationException("Clone/remap failed to reuse semantically equivalent content already present under another ID.");
var cloneManifest = DbcCloneRemapService.CreateManifest(castTimesSource, Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns, castResolution.KeyStrategy, [sourceCastId]);
var cloneOutputPath = Path.Combine(layerRoot, "SpellCastTimes.clone-remap.dbc");
DbcCloneRemapService.Apply(castTimesSource, Path.Combine(overrideLayer, "SpellCastTimes.dbc"), cloneOutputPath, castColumns, castResolution.KeyStrategy, cloneManifest);
var cloneOutput = WdbcFile.Load(cloneOutputPath); var cloneRows = DbcRecordIdentity.IndexRows(cloneOutput, castColumns, castResolution.KeyStrategy);
if (cloneManifest.Entries.Count != 1 || cloneOutput.RowCount != WdbcFile.Load(castTimesSource).RowCount + 1 ||
    cloneOutput.GetRaw(cloneRows[cloneManifest.Entries[0].TargetId], castColumns[1]) != castTimesOverride.GetRaw(0, castColumns[1]))
    throw new InvalidOperationException("Clone/remap did not preserve the original ID while copying the source record to a new identity.");
if (DbcCloneRemapService.FindReferencedIds(Path.Combine(overrideLayer, "SpellCastTimes.dbc"), castColumns, castResolution.KeyStrategy, [sourceCastId], castColumns[0].Name).Single() != sourceCastId)
    throw new InvalidOperationException("Clone dependency discovery did not read the selected parent foreign key.");
var clonedBaseValue = cloneOutput.GetRaw(cloneRows[cloneManifest.Entries[0].TargetId], castColumns[1]);
var referenceMap = new DbcCloneRemapManifest(1, "Fixture", "ID", "", "", [new(clonedBaseValue, clonedBaseValue + 1)]);
var referenceOutputPath = Path.Combine(layerRoot, "SpellCastTimes.reference-remap.dbc");
if (DbcCloneRemapService.ApplyReferenceMap(cloneOutputPath, referenceOutputPath, castColumns, castResolution.KeyStrategy, [cloneManifest.Entries[0].TargetId], castColumns[1].Name, referenceMap) != 1 ||
    WdbcFile.Load(referenceOutputPath).GetRaw(cloneRows[cloneManifest.Entries[0].TargetId], castColumns[1]) != clonedBaseValue + 1)
    throw new InvalidOperationException("Foreign-key remapping modified neither the selected cloned record nor its intended field.");
var copiedRowPath = Path.Combine(layerRoot, "SpellCastTimes.copy-row.dbc");
DbcRowMutationService.CopyRow(castTimesSource, Path.Combine(overrideLayer, "SpellCastTimes.dbc"), copiedRowPath, castColumns, castResolution.KeyStrategy, sourceCastId, 9_000_000, new Dictionary<string, string> { [castColumns[1].Name] = "123" });
var copiedRows = DbcRecordIdentity.IndexRows(WdbcFile.Load(copiedRowPath), castColumns, castResolution.KeyStrategy);
if (!copiedRows.ContainsKey(9_000_000) || WdbcFile.Load(copiedRowPath).GetRaw(copiedRows[9_000_000], castColumns[1]) != 123)
    throw new InvalidOperationException("Additive row copy with a field override failed.");
var setRowPath = Path.Combine(layerRoot, "SpellCastTimes.set-row.dbc");
DbcRowMutationService.SetRow(copiedRowPath, setRowPath, castColumns, castResolution.KeyStrategy, 9_000_000, new Dictionary<string, string> { [castColumns[1].Name] = "456" });
if (WdbcFile.Load(setRowPath).GetRaw(copiedRows[9_000_000], castColumns[1]) != 456)
    throw new InvalidOperationException("Selected row mutation failed.");

var semanticBasePath = Path.Combine(layerRoot, "AnimationData.semantic-base.dbc");
var semanticOverridePath = Path.Combine(layerRoot, "AnimationData.semantic-override.dbc");
var semanticBase = WdbcFile.Load(animationPath); semanticBase.SetDisplayValue(0, nameColumn, "Crucible_Same_Text"); semanticBase.Save(semanticBasePath, false);
var semanticOverride = WdbcFile.Load(animationPath); semanticOverride.SetDisplayValue(1, nameColumn, "Crucible_Padding_Text"); semanticOverride.SetDisplayValue(0, nameColumn, "Crucible_Same_Text"); semanticOverride.Save(semanticOverridePath, false);
if (WdbcFile.Load(semanticBasePath).GetRaw(0, nameColumn) == WdbcFile.Load(semanticOverridePath).GetRaw(0, nameColumn))
    throw new InvalidOperationException("Semantic comparison fixture did not create distinct string offsets.");
var semanticDetail = DbcLayerComparer.CompareFiles(semanticBasePath, semanticOverridePath, animationColumns, animationResolution.KeyStrategy);
if (semanticDetail.ModifiedCells != 1) throw new InvalidOperationException("Layer comparison treated equal decoded strings at different offsets as changes.");
var stringDifferences = DbcPromotionService.GetDifferences(semanticBasePath, semanticOverridePath, animationColumns, animationResolution.KeyStrategy);
var changedString = stringDifferences.Single(difference => difference.Id == semanticOverride.GetRaw(1, idColumn) && difference.ColumnIndex == nameColumn.Index);
var stringManifest = new DbcPromotionManifest(1, "AnimationData.semantic-base", idColumn.Name, [new(changedString.Id, [nameColumn.Name])]);
// Manifest table names deliberately follow the selected base filename, allowing arbitrary working-copy names.
var promotedAnimationPath = Path.Combine(layerRoot, "AnimationData.promoted.dbc");
DbcPromotionService.Apply(semanticBasePath, semanticOverridePath, promotedAnimationPath, animationColumns, animationResolution.KeyStrategy, stringManifest);
var promotedAnimation = WdbcFile.Load(promotedAnimationPath);
if (promotedAnimation.GetString(promotedAnimation.GetRaw(1, nameColumn)) != "Crucible_Padding_Text")
    throw new InvalidOperationException("Promoted string was not re-interned into the destination table.");

var stagingRoot = Path.Combine(layerRoot, "my-staging-folder"); Directory.CreateDirectory(Path.Combine(stagingRoot, "Interface", "FrameXML"));
if (!MpqPathFilter.Matches("DBFilesClient\\Spell.dbc", "DBFilesClient\\*.dbc") || MpqPathFilter.Matches("DBFilesClient\\Spell.dbc", "Interface\\*.dbc") || !MpqPathFilter.Matches("DBFilesClient\\Spell.dbc", "spell"))
    throw new InvalidOperationException("MPQ path filtering does not support both globs and plain-text searches.");
File.WriteAllText(Path.Combine(stagingRoot, "Interface", "FrameXML", "Test.lua"), "-- test");
var stagedEntry = PatchInputMapper.Map([stagingRoot]).Single();
if (stagedEntry.ArchivePath != "Interface\\FrameXML\\Test.lua" || PatchInputMapper.AssessArchivePath(stagedEntry.ArchivePath).HasWarning)
    throw new InvalidOperationException("Staging folder archive-root mapping failed.");
if (PatchInputMapper.Map([baseLayer]).Any(entry => !entry.ArchivePath.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("A generic folder of DBC files was not mapped beneath DBFilesClient.");
var provenanceRoot = Path.Combine(layerRoot, "provenance-extract"); var provenanceA = Path.Combine(provenanceRoot, "archive-a", "DBFilesClient"); var provenanceB = Path.Combine(provenanceRoot, "archive-b", "DBFilesClient");
Directory.CreateDirectory(provenanceA); Directory.CreateDirectory(provenanceB); File.Copy(animationPath, Path.Combine(provenanceA, "AnimationData.dbc")); File.Copy(animationPath, Path.Combine(provenanceB, "AnimationData.dbc"));
if (PatchInputMapper.MapCandidates([provenanceRoot]).Count(entry => entry.ArchivePath == "DBFilesClient\\AnimationData.dbc") != 2)
    throw new InvalidOperationException("Provenance-preserving patch mapping hid duplicate internal paths.");
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

var deploymentClient = Path.Combine(layerRoot, "deployment-client", "DBFilesClient"); var deploymentServer = Path.Combine(layerRoot, "deployment-server", "data", "dbc");
Directory.CreateDirectory(deploymentClient); Directory.CreateDirectory(deploymentServer);
File.Copy(animationPath, Path.Combine(deploymentClient, "AnimationData.dbc")); File.Copy(animationPath, Path.Combine(deploymentServer, "AnimationData.dbc"));
File.Copy(Path.Combine(overrideLayer, "SpellCastTimes.dbc"), Path.Combine(deploymentClient, "SpellCastTimes.dbc")); File.Copy(castTimesSource, Path.Combine(deploymentServer, "SpellCastTimes.dbc"));
File.WriteAllBytes(Path.Combine(deploymentClient, "CharVariations.dbc"), []); File.WriteAllBytes(Path.Combine(deploymentServer, "CharVariations.dbc"), []);
var deploymentSource = Path.Combine(layerRoot, "deployment-source"); var dataStores = Path.Combine(deploymentSource, "src", "server", "game", "DataStores"); Directory.CreateDirectory(dataStores);
File.WriteAllText(Path.Combine(dataStores, "DBCStores.cpp"), "LOAD_DBC(store, \"AnimationData.dbc\");\nLOAD_DBC(store, \"SpellCastTimes.dbc\");\nLOAD_DBC(store, \"CharVariations.dbc\");\n");
var workspace = new ServerWorkspace(Path.Combine(layerRoot, "deployment-server"), "fixture.conf", deploymentServer, ServerCoreFamily.AzerothCore, new("127.0.0.1", 3306, "test", "test", "test"));
if (ServerTableBindingCatalog.ResolveFile(ServerCoreFamily.AzerothCore, "ClientOnlyFixture.dbc", deploymentSource).Consumption != ServerTableConsumption.ClientOnly)
    throw new InvalidOperationException("A source-backed table absent from DBCStores was not classified as client-only.");
var deploymentPlan = ClientServerDeploymentPlanner.Analyze(deploymentClient, workspace, deploymentSource);
if (deploymentPlan.Entries.Single(entry => entry.DbcFileName == "AnimationData.dbc").Status != ClientServerPlanStatus.Identical ||
    deploymentPlan.Entries.Single(entry => entry.DbcFileName == "SpellCastTimes.dbc").Status != ClientServerPlanStatus.ServerDbcChange ||
    deploymentPlan.Entries.Single(entry => entry.DbcFileName == "CharVariations.dbc").Status != ClientServerPlanStatus.Identical)
    throw new InvalidOperationException("Client-to-server DBC planning did not distinguish identical and changed server-loaded tables.");
var deploymentStage = ClientServerDeploymentPlanner.Stage(Path.Combine(layerRoot, "deployment-stage"), deploymentPlan);
if (deploymentStage.ClientFiles != 1 || deploymentStage.ServerFiles != 1 || deploymentStage.PatchManifestPath is null ||
    !File.Exists(Path.Combine(deploymentStage.RootPath, "server-dbc", "SpellCastTimes.dbc")))
    throw new InvalidOperationException("Client-to-server safe staging did not create the expected separated outputs.");
var conflictingLayer = Path.Combine(deploymentClient, "nested-layer"); Directory.CreateDirectory(conflictingLayer); File.Copy(castTimesSource, Path.Combine(conflictingLayer, "SpellCastTimes.dbc"));
var conflictPlan = ClientServerDeploymentPlanner.Analyze(deploymentClient, workspace, deploymentSource);
if (conflictPlan.Entries.Single(entry => entry.DbcFileName == "SpellCastTimes.dbc").Status != ClientServerPlanStatus.ConflictingClientLayers)
    throw new InvalidOperationException("Different same-named extracted DBC layers were not blocked as a conflict.");
var fusionBase = Path.Combine(layerRoot, "fusion-base", "DBFilesClient"); var fusionA = Path.Combine(layerRoot, "fusion-a", "DBFilesClient"); var fusionB = Path.Combine(layerRoot, "fusion-b", "DBFilesClient");
Directory.CreateDirectory(fusionBase); Directory.CreateDirectory(fusionA); Directory.CreateDirectory(fusionB);
File.Copy(animationPath, Path.Combine(fusionBase, "AnimationData.dbc")); File.Copy(animationPath, Path.Combine(fusionA, "AnimationData.dbc"));
File.Copy(castTimesSource, Path.Combine(fusionBase, "SpellCastTimes.dbc")); File.Copy(Path.Combine(overrideLayer, "SpellCastTimes.dbc"), Path.Combine(fusionA, "SpellCastTimes.dbc")); File.Copy(castTimesSource, Path.Combine(fusionB, "SpellCastTimes.dbc"));
var fusionPlan = ClientFusionPlanner.Analyze(Path.GetDirectoryName(fusionBase)!, [new("Mod A", Path.GetDirectoryName(fusionA)!), new("Mod B", Path.GetDirectoryName(fusionB)!)]);
var savedFusionPlan = Path.Combine(layerRoot, "fusion-plan.json"); ClientFusionPlanner.Save(savedFusionPlan, fusionPlan);
if (fusionPlan.Entries.Single(entry => entry.ArchivePath.EndsWith("AnimationData.dbc", StringComparison.OrdinalIgnoreCase)).Status != ClientFusionStatus.IdenticalToBase ||
    fusionPlan.Entries.Single(entry => entry.ArchivePath.EndsWith("SpellCastTimes.dbc", StringComparison.OrdinalIgnoreCase)).Status != ClientFusionStatus.Conflict)
    throw new InvalidOperationException("Client fusion did not omit base-identical content or expose path conflicts.");
if (!File.Exists(savedFusionPlan) || !File.ReadAllText(savedFusionPlan).Contains("SpellCastTimes.dbc", StringComparison.Ordinal))
    throw new InvalidOperationException("Client fusion plan export did not preserve its entries.");
var fusionConflict = fusionPlan.Entries.Single(entry => entry.Status == ClientFusionStatus.Conflict); var fusionChoice = fusionConflict.Candidates.Single(candidate => candidate.SourceName == "Mod A");
var fusionStage = ClientFusionPlanner.Stage(Path.Combine(layerRoot, "fusion-stage"), fusionPlan, new Dictionary<string, string> { [fusionConflict.ArchivePath] = fusionChoice.FilePath });
if (fusionStage.StagedFiles != 1 || fusionStage.SkippedBaseFiles != 1 || fusionStage.UnresolvedConflicts != 0 || !File.Exists(fusionStage.ManifestPath))
    throw new InvalidOperationException("Resolved fusion staging did not produce a minimal patch manifest.");

var manifestPath = Path.Combine(layerRoot, "classless.crucible-patch.json");
var manifestEntries = PatchInputMapper.Map([Path.Combine(overrideLayer, "SpellCastTimes.dbc")]);
var manifestPolicy = new PatchManifestPolicy(["DBFilesClient\\*.dbc"], ["**\\*.m2", "**\\*.skin"], 1);
if (!PatchManifestService.ValidateEntries(manifestEntries, manifestPolicy).Passed || PatchManifestService.ValidateEntries(manifestEntries, manifestPolicy with { ExpectedEntryCount = 2 }).Passed)
    throw new InvalidOperationException("Manifest allowed-glob or exact-count validation failed.");
var forbiddenPolicyEntry = new PatchEntry(manifestEntries[0].SourcePath, "Character\\Test.m2");
var rootForbiddenPolicyEntry = forbiddenPolicyEntry with { ArchivePath = "Test.m2" };
if (PatchManifestService.ValidateEntries([forbiddenPolicyEntry], manifestPolicy with { AllowedGlobs = ["**"] }).Passed || PatchManifestService.ValidateEntries([rootForbiddenPolicyEntry], manifestPolicy with { AllowedGlobs = ["**"] }).Passed)
    throw new InvalidOperationException("Manifest forbidden-glob validation failed.");
if (PatchManifestService.ValidateEntries(manifestEntries, manifestPolicy with { RequiredGlobs = ["DBFilesClient\\Spell.dbc"] }).Passed ||
    !PatchManifestService.ValidateEntries(manifestEntries, manifestPolicy with { RequiredGlobs = ["DBFilesClient\\SpellCastTimes.dbc"] }).Passed)
    throw new InvalidOperationException("Manifest required-glob validation failed.");
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

var snapshotColumns = new[]
{
    new LegacyDatabaseSnapshotColumn("entry", 1, "int", "int unsigned", false, null, "PRI", "", null, null, null, 10, 0, null, null),
    new LegacyDatabaseSnapshotColumn("name", 2, "varchar", "varchar(255)", false, "", "", "", "utf8", "utf8_general_ci", 255, null, null, null, null)
};
LegacyDatabaseTableSchema SnapshotSchema(string name, string type = "BASE TABLE") => new(name, type, "InnoDB", "utf8_general_ci", "fixture", 1, name == "item_template" ? ["entry"] : [], snapshotColumns);
var snapshotSchemas = new[]
{
    SnapshotSchema("item_template"), SnapshotSchema("creature_template"), SnapshotSchema("playercreateinfo"),
    SnapshotSchema("mail_loot_template"), SnapshotSchema("instance_template"), SnapshotSchema("guild_rewards"), SnapshotSchema("pet_levelstats"),
    SnapshotSchema("character_inventory"), SnapshotSchema("account"), SnapshotSchema("mail"), SnapshotSchema("pet_aura"), SnapshotSchema("guild_member"), SnapshotSchema("instance_reset"),
    SnapshotSchema("item_view", "VIEW")
};
var selectedSnapshotTables = LegacyDatabaseSnapshotService.SelectTables(snapshotSchemas, new(), true, out var excludedSnapshotTables);
if (selectedSnapshotTables.Count != 7 || selectedSnapshotTables.Any(table => table.Name is "character_inventory" or "account" or "mail" or "pet_aura" or "guild_member" or "instance_reset" or "item_view") ||
    !selectedSnapshotTables.Any(table => table.Name == "mail_loot_template") || !selectedSnapshotTables.Any(table => table.Name == "instance_template") ||
    !selectedSnapshotTables.Any(table => table.Name == "guild_rewards") || !selectedSnapshotTables.Any(table => table.Name == "pet_levelstats") ||
    !excludedSnapshotTables.Contains("character_inventory") || !excludedSnapshotTables.Contains("item_view"))
    throw new InvalidOperationException("Legacy SQL snapshot safety did not retain world definitions while excluding account/character state and views.");
var deliberatelySensitive = LegacyDatabaseSnapshotService.SelectTables(snapshotSchemas, new(IncludeSensitiveState: true), true, out _);
if (deliberatelySensitive.Count != 13 || !LegacyDatabaseSnapshotService.GlobMatches("creature_template", "creature_*") || LegacyDatabaseSnapshotService.GlobMatches("item_template", "creature_*"))
    throw new InvalidOperationException("Legacy SQL snapshot explicit inclusion or table glob matching failed.");
try
{
    _ = LegacyDatabaseSnapshotService.SelectTables(snapshotSchemas, new(IncludePatterns: [""]), true, out _);
    throw new InvalidOperationException("Legacy SQL snapshot accepted an empty include pattern that would unexpectedly select every table.");
}
catch (ArgumentException exception) when (exception.Message.Contains("cannot be empty", StringComparison.Ordinal)) { }

var snapshotArtifact = Path.Combine(layerRoot, "fixture.crucible-db-snapshot");
var snapshotSchema = SnapshotSchema("item_template"); var snapshotSchemaHash = LegacyDatabaseSnapshotService.ComputeSchemaHash(snapshotSchema);
if (LegacyDatabaseSnapshotService.ComputeSchemaHash(snapshotSchema with { EstimatedRows = 999 }) != snapshotSchemaHash)
    throw new InvalidOperationException("Legacy SQL schema fingerprints incorrectly depend on estimated row counts.");
var snapshotRows = System.Text.Encoding.UTF8.GetBytes("[[\"1\",\"Crucible Sword\"]]");
var snapshotRowsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshotRows)).ToLowerInvariant();
var snapshotTable = new LegacyDatabaseSnapshotTable(snapshotSchema.Name, snapshotSchema.TableType, snapshotSchema.Engine, snapshotSchema.Collation, snapshotSchema.Comment,
    snapshotSchema.EstimatedRows, snapshotSchema.PrimaryKey, snapshotSchema.Columns, snapshotSchemaHash, "tables/item_template.rows.json", "entry", 1, snapshotRows.Length, snapshotRowsHash);
var snapshotManifest = new LegacyDatabaseSnapshotManifest(LegacyDatabaseSnapshotService.ArtifactFormat, LegacyDatabaseSnapshotService.ArtifactFormatVersion, "fixture", DateTimeOffset.UtcNow,
    new("legacy_world", "fixture server", "fixture database", "utf8", "utf8_general_ci", new Dictionary<string, string> { ["core_version"] = "fixture" }),
    new([], [], false, ["account"]), [snapshotTable], 1,
    LegacyDatabaseSnapshotService.ComputeSchemaAggregateHash([(snapshotTable.Name, snapshotTable.SchemaSha256)]),
    LegacyDatabaseSnapshotService.ComputeAggregateHash([(snapshotTable.Name, snapshotTable.RowsSha256, snapshotTable.Rows)]), true, true);
using (var stream = File.Create(snapshotArtifact))
using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
{
    using (var rowsStream = archive.CreateEntry(snapshotTable.DataEntry).Open()) rowsStream.Write(snapshotRows);
    using var manifestStream = archive.CreateEntry("manifest.json").Open();
    System.Text.Json.JsonSerializer.Serialize(manifestStream, snapshotManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
var snapshotInspection = new LegacyDatabaseSnapshotService().InspectAsync(snapshotArtifact).GetAwaiter().GetResult();
if (!snapshotInspection.Valid || snapshotInspection.Manifest?.TotalRows != 1 || System.Text.Json.JsonSerializer.Serialize(snapshotInspection.Manifest).Contains("password", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Portable legacy SQL snapshot validation failed: {string.Join("; ", snapshotInspection.Findings)}");
using (var stream = File.Open(snapshotArtifact, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Update))
{
    archive.GetEntry("manifest.json")!.Delete();
    using var manifestStream = archive.CreateEntry("manifest.json").Open();
    System.Text.Json.JsonSerializer.Serialize(manifestStream, snapshotManifest with { TotalRows = 2 }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
var corruptSnapshotInspection = new LegacyDatabaseSnapshotService().InspectAsync(snapshotArtifact).GetAwaiter().GetResult();
if (corruptSnapshotInspection.Valid || !corruptSnapshotInspection.Findings.Any(finding => finding.Contains("total row count", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Legacy SQL snapshot validation did not reject a tampered aggregate row count.");
Directory.Delete(layerRoot, true);

Console.WriteLine($"PASS: loaded {loaded:N0} WDBC files, cloned 100 real spells in {spellBulkCloneMilliseconds:N0} ms, verified persistence, layered comparison, manifest build, and MPQ workflows.");

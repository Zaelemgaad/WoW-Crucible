using WoWCrucible.Core;
using System.Diagnostics;

if (args.Length != 2)
    throw new ArgumentException("Usage: WoWCrucible.Core.Tests <schema.xml> <dbc-directory>");

var itemDisplayPath = Path.Combine(args[1], "ItemDisplayInfo.dbc");
var dbdFixture=Path.Combine(Path.GetTempPath(),$"crucible-dbd-{Guid.NewGuid():N}.dbd");
try
{
    File.WriteAllText(dbdFixture,"""
COLUMNS
int ID
string Name
locstring Label_lang
float Values

BUILD 3.0.1.8303-3.3.5.12340
$id$ID<32>
Name
Label_lang
Values[2]

BUILD 4.0.0.11792-4.3.4.15595
$noninline,id$ID<32>
Name
Values[3]
""");
    var fixture=DbdSchemaService.Load(dbdFixture);var wrathColumns=DbdSchemaService.ResolveColumns(fixture,12340);var cataColumns=DbdSchemaService.ResolveColumns(fixture,15595);
    if(wrathColumns.Count!=21||cataColumns.Count!=4||fixture.ForBuild(12340)?.Builds.Single().Start.Major!=3||fixture.ForBuild(15595)?.Builds.Single().Start.Major!=4)
        throw new InvalidOperationException("DBD full-version build selection, localized-string expansion, arrays, or non-inline fields regressed.");
}
finally { if(File.Exists(dbdFixture))File.Delete(dbdFixture); }
var workspaceRoot=Directory.GetParent(Directory.GetCurrentDirectory())?.FullName;var localDbdRoot=workspaceRoot is null?string.Empty:Path.Combine(workspaceRoot,"Tools","WoWDBDefs","definitions");
if(Directory.Exists(localDbdRoot))
{
    var itemDisplayDbd=DbdSchemaService.Load(Path.Combine(localDbdRoot,"ItemDisplayInfo.dbd"));var charSectionsDbd=DbdSchemaService.Load(Path.Combine(localDbdRoot,"CharSections.dbd"));var spellDbd=DbdSchemaService.Load(Path.Combine(localDbdRoot,"Spell.dbd"));
    if(DbdSchemaService.ResolveColumns(itemDisplayDbd,12340).Count!=25||DbdSchemaService.ResolveColumns(charSectionsDbd,12340).Count!=10||DbdSchemaService.ResolveColumns(spellDbd,12340).Count!=234)
        throw new InvalidOperationException("WoWDBDefs build-range resolution did not expand the real build-12340 ItemDisplayInfo, CharSections, and Spell layouts to their exact WDBC field counts.");
    var dbdAudit=DbdSchemaService.Audit(localDbdRoot,args[1],12340,args[0]);if(dbdAudit.Rows.Count<246||dbdAudit.Matches<236||dbdAudit.EmptyPlaceholders!=1||dbdAudit.Failures!=9||dbdAudit.Rows.Count(row=>row.Status==DbdAuditStatus.InvalidDefinition)>0)
        throw new InvalidOperationException($"WoWDBDefs corpus audit was incomplete or invalid: rows={dbdAudit.Rows.Count}, matches={dbdAudit.Matches}, empty={dbdAudit.EmptyPlaceholders}, failures={dbdAudit.Failures}, invalid={dbdAudit.Rows.Count(row=>row.Status==DbdAuditStatus.InvalidDefinition)}.");
}
var spellTooltipCatalog=SpellTooltipService.Load(Path.Combine(args[1],"Spell.dbc"));
if(spellTooltipCatalog.Records.Count<40000||!spellTooltipCatalog.Records.TryGetValue(133,out var fireballTooltip)||fireballTooltip.Name!="Fireball"||string.IsNullOrWhiteSpace(fireballTooltip.Description)||SpellTooltipService.Clean("|cffffffff  A\r\n B  |r")!="A B")
    throw new InvalidOperationException("Cached WotLK spell tooltip decoding did not preserve real names/descriptions or clean client color/whitespace codes.");
var martinDisplay = ItemDisplayInfoService.Resolve(itemDisplayPath, args[0], 7016, 4, 4, 4);
if (martinDisplay.InventoryIcons.FirstOrDefault() != "INV_Chest_Samurai" || martinDisplay.ModelNames.Any(value => value.Length > 0) ||
    !martinDisplay.Assets.Any(asset => asset.Kind == "wear-texture" && asset.ClientPaths.Any(path => path.EndsWith(@"Item\TextureComponents\ArmUpperTexture\Plate_A_01Silver_Sleeve_AU.blp", StringComparison.OrdinalIgnoreCase)) &&
        asset.ClientPaths.Any(path => path.EndsWith(@"Plate_A_01Silver_Sleeve_AU_F.blp", StringComparison.OrdinalIgnoreCase)) && asset.ClientPaths.Any(path => path.EndsWith(@"Plate_A_01Silver_Sleeve_AU_M.blp", StringComparison.OrdinalIgnoreCase))))
    throw new InvalidOperationException("ItemDisplayInfo resolution did not preserve the real armor icon and wearable texture-slot paths for display 7016.");
var thunderfuryDisplay = ItemDisplayInfoService.Resolve(itemDisplayPath, null, 30606, 2, 8, 17);
if (thunderfuryDisplay.ModelNames.FirstOrDefault() != "Sword_2H_Ashbringer02.mdx" ||
    !thunderfuryDisplay.Assets.Any(asset => asset.Kind == "model" && asset.ClientPaths.First().Equals(@"Item\ObjectComponents\Weapon\Sword_2H_Ashbringer02.m2", StringComparison.OrdinalIgnoreCase)) ||
    !thunderfuryDisplay.Assets.Any(asset => asset.Kind == "model-texture" && asset.ClientPaths.First().EndsWith(@"Sword_2H_Ashbringer_A_01Blue.blp", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Built-in ItemDisplayInfo resolution did not map a real WotLK weapon display to canonical M2 and texture paths.");
var chestGeosets = ItemEquipmentPreviewService.ResolveGeosets(5, [2, 3, 4]);
var legGeosets = ItemEquipmentPreviewService.ResolveGeosets(7, [1, 2, 3]);
var bootGeosets = ItemEquipmentPreviewService.ResolveGeosets(8, [4, 5, 0]);
if (chestGeosets.GroupVariants[8] != 3 || chestGeosets.GroupVariants[10] != 4 || chestGeosets.GroupVariants[13] != 5 ||
    legGeosets.GroupVariants[11] != 2 || legGeosets.GroupVariants[9] != 3 || legGeosets.GroupVariants[13] != 4 ||
    bootGeosets.GroupVariants[5] != 5 || bootGeosets.GroupVariants[20] != 6 || ItemEquipmentPreviewService.ResolveGeosets(2, [9, 9, 9]).GroupVariants.Count != 0)
    throw new InvalidOperationException("Item inventory types did not resolve to the verified Wrath character geoset groups and one-based variants.");
var wearSourceFixture = Path.Combine(Path.GetTempPath(), $"crucible-wear-source-{Guid.NewGuid():N}"); var wearProvenance = Path.Combine(wearSourceFixture, "patch-test"); Directory.CreateDirectory(wearProvenance);
var femaleWear0 = Path.Combine(wearProvenance, "fixture_AU_F.blp"); var femaleWear3 = Path.Combine(wearProvenance, "fixture_TU_F.blp"); var maleWear0 = Path.Combine(wearProvenance, "fixture_AU_M.blp"); var maleWear3 = Path.Combine(wearProvenance, "fixture_TU_M.blp");
foreach (var path in new[] { femaleWear0, femaleWear3, maleWear0, maleWear3 }) File.WriteAllBytes(path, [1]);
var wearAssets = new[]
{
    new ItemDisplayAsset("wear-texture",0,"fixture_AU",[],[femaleWear0,maleWear0]),
    new ItemDisplayAsset("wear-texture",3,"fixture_TU",[],[femaleWear3,maleWear3])
};
var wearDisplay = martinDisplay with { Assets = wearAssets, WearTextures = new[] { "fixture_AU", "", "", "fixture_TU", "", "", "", "" } };
var wearSources = ItemEquipmentPreviewService.FindWearSources(wearDisplay);
if (wearSources.Count != 2 || wearSources.Any(source => source.SlotFiles.Count != 2) || !wearSources.Any(source => source.Source == "patch-test · female") || !wearSources.Any(source => source.Source == "patch-test · male"))
    throw new InvalidOperationException("Gendered item wear textures were mixed across provenance choices.");
Directory.Delete(wearSourceFixture,true);

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

var toolInventoryFixture = Path.Combine(Path.GetTempPath(), $"crucible-tool-inventory-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(toolInventoryFixture, "Keira3")); Directory.CreateDirectory(Path.Combine(toolInventoryFixture, "WDBXEditor")); Directory.CreateDirectory(Path.Combine(toolInventoryFixture, "MysteryTool"));
Directory.CreateDirectory(Path.Combine(toolInventoryFixture, "Tools", "MPQEditor future")); Directory.CreateDirectory(Path.Combine(toolInventoryFixture, "Tools", "Models", "anim porter")); Directory.CreateDirectory(Path.Combine(toolInventoryFixture, "Tools", "NewNestedTool"));
var toolInventory = ToolConsolidationInventoryService.Scan(toolInventoryFixture); var discoveredToolRoot = ToolConsolidationInventoryService.FindWorkspaceRoot(Path.Combine(toolInventoryFixture, "Tools", "Models"));
if (toolInventory.Unassigned != 2 || toolInventory.Missing == 0 || !toolInventory.Entries.Any(entry => entry.RelativePath == "Keira3" && entry.Status == ToolInventoryStatus.Tracked) ||
    !toolInventory.Entries.Any(entry => entry.RelativePath == "Tools/MPQEditor future" && entry.Status == ToolInventoryStatus.Tracked) || !toolInventory.Entries.Any(entry => entry.RelativePath == "Tools/Models/anim porter" && entry.Status == ToolInventoryStatus.Tracked) ||
    !toolInventory.Entries.Any(entry => entry.RelativePath == "MysteryTool" && entry.Status == ToolInventoryStatus.Unassigned) || !toolInventory.Entries.Any(entry => entry.RelativePath == "Tools/NewNestedTool" && entry.Status == ToolInventoryStatus.Unassigned) || !discoveredToolRoot.Equals(toolInventoryFixture, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Tool consolidation inventory did not distinguish assigned, missing, and newly unassigned workspace/tool roots.");
Directory.Delete(toolInventoryFixture, true);

var browserEntries = new MpqFileEntry[]
{
    new(@"Character\Human\Male\HumanMale.m2", 100, 60, 0, 0),
    new(@"Character\Human\Male\HumanMale00.skin", 50, 25, 0, 0),
    new(@"Character\Human\Female\HumanFemale.m2", 110, 70, 0, 0),
    new(@"DBFilesClient\Spell.dbc", 200, 100, 0, 0),
    new("File00000123.xxx", 25, 25, 0, 0),
    new("(listfile)", 10, 10, 0, 0)
};
var browserRoot = MpqArchiveBrowser.Browse(browserEntries, null); var humanFolder = MpqArchiveBrowser.Browse(browserEntries, @"Character\Human");
if (browserRoot.Nodes.Count != 4 || browserRoot.Nodes.Count(node => node.IsFolder) != 2 || browserRoot.RecursiveFiles != 6 || browserRoot.AnonymousFiles != 1 ||
    humanFolder.Nodes.Count != 2 || humanFolder.Nodes.Any(node => !node.IsFolder) || !humanFolder.Breadcrumbs.SequenceEqual(["Character", @"Character\Human"]) || MpqArchiveBrowser.Parent(@"Character\Human") != "Character")
    throw new InvalidOperationException("Lazy MPQ folder browsing did not preserve hierarchy, breadcrumbs, metadata, or anonymous entries.");
var maleNode = humanFolder.Nodes.Single(node => node.Name == "Male"); var selectedMale = MpqArchiveBrowser.Select(browserEntries, [maleNode]);
if (selectedMale.Count != 2 || selectedMale.Any(entry => !entry.ArchivePath.StartsWith(@"Character\Human\Male\", StringComparison.Ordinal)) || MpqArchiveBrowser.SelectFolder(browserEntries, "").Count != browserEntries.Length)
    throw new InvalidOperationException("MPQ folder selection did not resolve exactly the recursive extraction closure.");
try { _ = MpqArchiveBrowser.Browse(browserEntries, @"Character\..\DBFilesClient"); throw new InvalidOperationException("MPQ folder navigation accepted a parent traversal segment."); }
catch (ArgumentException) { }
var mpqCacheFixture = Path.Combine(Path.GetTempPath(), $"crucible-mpq-cache-{Guid.NewGuid():N}"); var fakeArchive = Path.Combine(mpqCacheFixture, "fixture.mpq"); var fakeCache = Path.Combine(mpqCacheFixture, "cache"); Directory.CreateDirectory(mpqCacheFixture); File.WriteAllText(fakeArchive, "fixture-v1"); var cacheLoads = 0;
var firstIndex = MpqArchiveIndexCache.LoadOrCreate(fakeArchive, null, fakeCache, () => { cacheLoads++; return browserEntries; });
var secondIndex = MpqArchiveIndexCache.LoadOrCreate(fakeArchive, null, fakeCache, () => { cacheLoads++; return []; });
if (firstIndex.Cached || !secondIndex.Cached || cacheLoads != 1 || secondIndex.Entries.Count != browserEntries.Length || !firstIndex.CachePath.StartsWith(fakeCache, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("App-local MPQ indexing did not reuse an identity-matched compressed cache.");
File.AppendAllText(fakeArchive, "-changed"); var changedIndex = MpqArchiveIndexCache.LoadOrCreate(fakeArchive, null, fakeCache, () => { cacheLoads++; return browserEntries[..2]; });
if (changedIndex.Cached || cacheLoads != 2 || changedIndex.Entries.Count != 2) throw new InvalidOperationException("MPQ index cache did not invalidate after the archive identity changed.");
File.WriteAllText(changedIndex.CachePath, "corrupt"); var repairedIndex = MpqArchiveIndexCache.LoadOrCreate(fakeArchive, null, fakeCache, () => { cacheLoads++; return browserEntries[..3]; });
if (repairedIndex.Cached || cacheLoads != 3 || repairedIndex.Entries.Count != 3) throw new InvalidOperationException("A corrupt MPQ index cache was not discarded and rebuilt safely.");
Directory.Delete(mpqCacheFixture, true);

var textureFixture = Path.Combine(Path.GetTempPath(), $"crucible-native-textures-{Guid.NewGuid():N}"); Directory.CreateDirectory(textureFixture);
var atomicExtractTarget = Path.Combine(textureFixture, "atomic-extract.bin"); File.WriteAllText(atomicExtractTarget, "known-good");
try
{
    PatchArchiveService.ExtractFileAtomically(atomicExtractTarget, true,
        temporary => { File.WriteAllText(temporary, "partial-garbage"); return false; },
        () => throw new IOException("simulated native extraction failure"));
    throw new InvalidOperationException("A simulated failed native extraction unexpectedly succeeded.");
}
catch (IOException exception) when (exception.Message.Contains("simulated native extraction failure", StringComparison.Ordinal)) { }
if (File.ReadAllText(atomicExtractTarget) != "known-good" || Directory.EnumerateFiles(textureFixture, "*.extracting").Any())
    throw new InvalidOperationException("Atomic MPQ extraction did not preserve the existing destination or clean its partial temporary file after failure.");
var texturePixels = new byte[8 * 8 * 4];
for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
{
    var offset = (y * 8 + x) * 4; texturePixels[offset] = (byte)(x * 31); texturePixels[offset + 1] = (byte)(y * 31);
    texturePixels[offset + 2] = (byte)((x + y) * 15); texturePixels[offset + 3] = (byte)((x * 8 + y) * 4);
}
var rgbaFixture = new RgbaTexture(8, 8, texturePixels); var nativePng = Path.Combine(textureFixture, "fixture.png");
BlpTextureService.WritePng(nativePng, rgbaFixture);
foreach (var format in new[] { BlpOutputFormat.Dxt1, BlpOutputFormat.Dxt1Alpha, BlpOutputFormat.Dxt3, BlpOutputFormat.Dxt5 })
{
    var blp = Path.Combine(textureFixture, $"fixture-{format}.blp");
    BlpTextureService.EncodeBlp2(rgbaFixture, blp, new(format, true, BlpOutputQuality.Balanced));
    var info = BlpTextureService.Inspect(blp); var decoded = BlpTextureService.Decode(blp);
    if (info.Version != BlpTextureVersion.Blp2 || info.MipLevels.Count != 4 || decoded.Width != 8 || decoded.Height != 8 || decoded.Pixels.Length != texturePixels.Length)
        throw new InvalidOperationException($"Native BLP {format} encode/decode did not preserve the texture shape and mip chain.");
}
var autoBlp = Path.Combine(textureFixture, "fixture-auto.blp");
BlpTextureService.EncodeFromImage(nativePng, autoBlp);
if (BlpTextureService.Inspect(autoBlp).Encoding != "DXT5" || BlpTextureService.Validate(textureFixture).Any(result => !result.Valid))
    throw new InvalidOperationException("Native PNG input, automatic alpha format selection, or BLP validation failed.");
var emptyBlp = Path.Combine(textureFixture, "fixture-empty.blp"); File.WriteAllBytes(emptyBlp, []); var emptyResult = BlpTextureService.Validate(emptyBlp).Single();
if (emptyResult.Valid || emptyResult.Error is null || !emptyResult.Error.Contains("zero-byte", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Zero-byte BLP validation did not report a precise extraction-artifact diagnosis.");
File.Delete(emptyBlp);
var zeroFilledBlp = Path.Combine(textureFixture, "fixture-zero-filled.blp"); File.WriteAllBytes(zeroFilledBlp, new byte[4096]); var zeroFilledResult = BlpTextureService.Validate(zeroFilledBlp).Single();
if (zeroFilledResult.Valid || zeroFilledResult.Error is null || !zeroFilledResult.Error.Contains("only zero bytes", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Zero-filled source payload validation did not distinguish the invalid archive entry from an ordinary unknown signature.");
File.Delete(zeroFilledBlp);
var rawBlp = Path.Combine(textureFixture, "fixture-raw3.blp"); var rawBytes = new byte[148 + texturePixels.Length];
System.Text.Encoding.ASCII.GetBytes("BLP2").CopyTo(rawBytes, 0); BitConverter.GetBytes((uint)1).CopyTo(rawBytes, 4); rawBytes[8] = 3; rawBytes[9] = 8; rawBytes[10] = 8;
BitConverter.GetBytes((uint)8).CopyTo(rawBytes, 12); BitConverter.GetBytes((uint)8).CopyTo(rawBytes, 16); BitConverter.GetBytes((uint)148).CopyTo(rawBytes, 20); BitConverter.GetBytes((uint)texturePixels.Length).CopyTo(rawBytes, 84);
for (var pixel = 0; pixel < 64; pixel++) { rawBytes[148 + pixel * 4] = texturePixels[pixel * 4 + 2]; rawBytes[149 + pixel * 4] = texturePixels[pixel * 4 + 1]; rawBytes[150 + pixel * 4] = texturePixels[pixel * 4]; rawBytes[151 + pixel * 4] = texturePixels[pixel * 4 + 3]; }
File.WriteAllBytes(rawBlp, rawBytes); var rawDecoded = BlpTextureService.Decode(rawBlp);
if (BlpTextureService.Inspect(rawBlp).Encoding != "BGRA8888" || !rawDecoded.Pixels.SequenceEqual(texturePixels))
    throw new InvalidOperationException("Native raw BGRA BLP2 decoding changed channel or alpha bytes.");
var truncatedBlp = Path.Combine(textureFixture, "fixture-truncated.blp"); File.WriteAllBytes(truncatedBlp, rawBytes[..200]); var truncatedResult = BlpTextureService.Validate(truncatedBlp).Single();
if (truncatedResult.Valid || truncatedResult.Error is null || !truncatedResult.Error.Contains("Truncated/corrupt BLP payload", StringComparison.OrdinalIgnoreCase) || !truncatedResult.Error.Contains("physical file", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Truncated top-level BLP payload validation did not report declared-versus-physical size evidence.");
File.Delete(truncatedBlp);
var cumulativeBlp = Path.Combine(textureFixture, "fixture-cumulative-ends.blp"); var cumulativeBytes = File.ReadAllBytes(autoBlp);
for (var mip = 0; mip < 16; mip++)
{
    var offset = BitConverter.ToUInt32(cumulativeBytes, 20 + mip * 4); var size = BitConverter.ToUInt32(cumulativeBytes, 84 + mip * 4);
    BitConverter.GetBytes(uint.MaxValue).CopyTo(cumulativeBytes, 20 + mip * 4); BitConverter.GetBytes(offset == 0 || size == 0 ? 0u : checked(offset + size)).CopyTo(cumulativeBytes, 84 + mip * 4);
}
File.WriteAllBytes(cumulativeBlp, cumulativeBytes); var cumulativeInfo = BlpTextureService.Inspect(cumulativeBlp); var cumulativeDecoded = BlpTextureService.Decode(cumulativeBlp);
if (cumulativeDecoded.Width != 8 || cumulativeDecoded.Height != 8 || !cumulativeInfo.Warnings.Any(warning => warning.Contains("cumulative", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Native BLP inspection did not recover the legacy cumulative-end mip-table exporter defect.");
var phantomBlp = Path.Combine(textureFixture, "fixture-phantom-mip.blp"); var phantomBytes = File.ReadAllBytes(autoBlp);
BitConverter.GetBytes((uint)phantomBytes.Length).CopyTo(phantomBytes, 20 + 4 * 4); BitConverter.GetBytes(uint.MaxValue).CopyTo(phantomBytes, 84 + 4 * 4); File.WriteAllBytes(phantomBlp, phantomBytes);
var phantomInfo = BlpTextureService.Inspect(phantomBlp);
if (phantomInfo.MipLevels.Count != 4 || !phantomInfo.Warnings.Any(warning => warning.Contains("phantom mip", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Native BLP inspection did not isolate a corrupt mip slot after a complete 1x1 chain.");
var nativeLibrary = Path.Combine(textureFixture, "library"); var nativeLibraryBlp = Path.Combine(nativeLibrary, "Archives", "Content", "Interface", "Test", "fixture-source", "native.blp");
Directory.CreateDirectory(Path.GetDirectoryName(nativeLibraryBlp)!); File.Copy(autoBlp, nativeLibraryBlp); File.WriteAllText(Path.Combine(nativeLibrary, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(textureFixture, nativeLibrary, long.MaxValue, DateTimeOffset.UtcNow, 0, [])));
var nativeRepair = BulkAssetLibraryService.RepairConversionsAsync(nativeLibrary, 1).GetAwaiter().GetResult();
if (nativeRepair.NewlyConvertedPngs != 1 || nativeRepair.RemainingFailures != 0 || !File.Exists(Path.ChangeExtension(nativeLibraryBlp, ".png")))
    throw new InvalidOperationException("Bulk asset-library repair did not use the native BLP decoder without an external converter.");
var artifactSourceRoot = Path.Combine(textureFixture, "artifact-source"); Directory.CreateDirectory(artifactSourceRoot);
var artifactArchive = Path.Combine(artifactSourceRoot, "artifact-source.mpq"); var zeroSource = Path.Combine(textureFixture, "source-zero.blp"); File.WriteAllBytes(zeroSource, new byte[512]);
new PatchArchiveService().Create(artifactArchive,
[
    new PatchEntry(autoBlp, @"Interface\Test\valid.blp"),
    new PatchEntry(zeroSource, @"Interface\Test\zero.blp")
]);
var artifactLibrary = Path.Combine(textureFixture, "artifact-library"); var artifactPlan = BulkAssetLibraryService.CreatePlan(artifactSourceRoot, artifactLibrary, long.MaxValue);
var artifactProvenance = $"artifact-source-{artifactPlan.Archives.Single().Identity}"; var artifactDirectory = Path.Combine(artifactLibrary, "Archives", "Content", "Interface", "Test", artifactProvenance); Directory.CreateDirectory(artifactDirectory);
var partialProcessed = Path.Combine(artifactDirectory, "valid.blp"); File.WriteAllBytes(partialProcessed, File.ReadAllBytes(autoBlp)[..160]);
var zeroProcessed = Path.Combine(artifactDirectory, "zero.blp"); File.WriteAllBytes(zeroProcessed, new byte[512]);
var artifactDryRun = BulkAssetLibraryService.RepairArchiveArtifacts(artifactLibrary, false);
if (artifactDryRun.InvalidArtifacts != 2 || artifactDryRun.Recovered != 1 || artifactDryRun.SourceInvalid != 1 || artifactDryRun.Quarantined != 0 || !File.Exists(partialProcessed) || !File.Exists(zeroProcessed))
    throw new InvalidOperationException("Archive artifact repair dry run changed files or failed to distinguish a recoverable partial extraction from a source-invalid entry.");
var artifactApplied = BulkAssetLibraryService.RepairArchiveArtifacts(artifactLibrary, true);
if (artifactApplied.InvalidArtifacts != 2 || artifactApplied.Recovered != 1 || artifactApplied.SourceInvalid != 1 || artifactApplied.Quarantined != 1 || !File.Exists(partialProcessed) || File.Exists(zeroProcessed) || BlpTextureService.Validate(partialProcessed).Single().Valid == false || !Directory.EnumerateFiles(Path.Combine(artifactLibrary, "Reports", "InvalidArchiveArtifacts"), "zero.blp", SearchOption.AllDirectories).Any())
    throw new InvalidOperationException("Archive artifact repair did not atomically recover the valid source or quarantine the proven source-invalid generated artifact.");
Directory.Delete(textureFixture, true);

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
var textureLookupOffset = geometryBytes.Length - 2; BitConverter.GetBytes((uint)1).CopyTo(geometryBytes, 0x80); BitConverter.GetBytes((uint)textureLookupOffset).CopyTo(geometryBytes, 0x84); BitConverter.GetBytes((ushort)0).CopyTo(geometryBytes, textureLookupOffset);
var embeddedFixturePath = @"Character\BloodElf\Female\fixture.blp"; var embeddedFixtureBytes = System.Text.Encoding.ASCII.GetBytes(embeddedFixturePath + "\0");
BitConverter.GetBytes((uint)0).CopyTo(geometryBytes, textureDefinitionOffset); BitConverter.GetBytes((uint)3).CopyTo(geometryBytes, textureDefinitionOffset + 4); BitConverter.GetBytes((uint)embeddedFixtureBytes.Length).CopyTo(geometryBytes, textureDefinitionOffset + 8); BitConverter.GetBytes((uint)(textureDefinitionOffset + 16)).CopyTo(geometryBytes, textureDefinitionOffset + 12); embeddedFixtureBytes.CopyTo(geometryBytes, textureDefinitionOffset + 16);
var fixtureVertices = new[] { (X: -1f, Y: 0f, Z: -1f), (X: 1f, Y: 0f, Z: -1f), (X: 0f, Y: 0f, Z: 1f) };
for (var index = 0; index < fixtureVertices.Length; index++)
{
    var offset = 0x130 + index * 48; BitConverter.GetBytes(fixtureVertices[index].X).CopyTo(geometryBytes, offset); BitConverter.GetBytes(fixtureVertices[index].Y).CopyTo(geometryBytes, offset + 4); BitConverter.GetBytes(fixtureVertices[index].Z).CopyTo(geometryBytes, offset + 8); BitConverter.GetBytes(1f).CopyTo(geometryBytes, offset + 28);
}
File.WriteAllBytes(geometryModelPath, geometryBytes);
var geometrySkin = new byte[240]; System.Text.Encoding.ASCII.GetBytes("SKIN").CopyTo(geometrySkin, 0); BitConverter.GetBytes((uint)3).CopyTo(geometrySkin, 4); BitConverter.GetBytes((uint)48).CopyTo(geometrySkin, 8); BitConverter.GetBytes((uint)9).CopyTo(geometrySkin, 12); BitConverter.GetBytes((uint)54).CopyTo(geometrySkin, 16); BitConverter.GetBytes((uint)3).CopyTo(geometrySkin, 28); BitConverter.GetBytes((uint)72).CopyTo(geometrySkin, 32); BitConverter.GetBytes((uint)1).CopyTo(geometrySkin, 36); BitConverter.GetBytes((uint)216).CopyTo(geometrySkin, 40);
for (ushort index = 0; index < 3; index++) BitConverter.GetBytes(index).CopyTo(geometrySkin, 48 + index * 2);
for (ushort index = 0; index < 9; index++) BitConverter.GetBytes((ushort)(index % 3)).CopyTo(geometrySkin, 54 + index * 2);
BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 72); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 78); BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 80); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 82);
BitConverter.GetBytes((ushort)2).CopyTo(geometrySkin, 120); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 126); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 128); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 130);
BitConverter.GetBytes((ushort)702).CopyTo(geometrySkin, 168); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 174); BitConverter.GetBytes((ushort)6).CopyTo(geometrySkin, 176); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 178);
geometrySkin[216] = 16; BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 220); BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 222); BitConverter.GetBytes((short)-1).CopyTo(geometrySkin, 224); BitConverter.GetBytes((ushort)1).CopyTo(geometrySkin, 230); BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 232);
File.WriteAllBytes(Path.Combine(assetFixture, "geometry00.skin"), geometrySkin);
var previewGeometry = M2PreviewGeometryService.Load(geometryModelPath);
var allGeosetGeometry = M2PreviewGeometryService.Load(geometryModelPath, visibilityMode: M2PreviewVisibilityMode.AllGeosets);
var selectedHairGeometry = M2PreviewGeometryService.Load(geometryModelPath, geosetSelection: new M2GeosetSelection(new Dictionary<int, int> { [0] = 2 }, "test selection"));
var nakedGeometry = M2PreviewGeometryService.Load(geometryModelPath, geosetSelection: new M2GeosetSelection(M2GeosetCatalog.NakedCharacterSelection, "naked test"));
var describedGeosets = M2GeosetCatalog.Describe(allGeosetGeometry.Submeshes); var describedHair = describedGeosets.Single(group => group.Group == 0);
if (M2GeosetCatalog.GroupName(4) != "Hands / gloves" || M2GeosetCatalog.GroupName(99) != "Unknown/custom group 99" || describedHair.Variants.Select(variant => variant.Variant).SequenceEqual([0, 2]) == false || describedHair.Variants.Sum(variant => variant.Triangles) != 2)
    throw new InvalidOperationException("Decoded M2 geoset catalog did not preserve named groups, exact variants, submeshes, and triangle counts.");
if (!M2PreviewGeometryService.IsBaseAppearanceGeoset(0) || !M2PreviewGeometryService.IsBaseAppearanceGeoset(401) || !M2PreviewGeometryService.IsBaseAppearanceGeoset(1301) ||
    M2PreviewGeometryService.IsBaseAppearanceGeoset(3) || M2PreviewGeometryService.IsBaseAppearanceGeoset(1201) || M2PreviewGeometryService.IsBaseAppearanceGeoset(1501) ||
    M2PreviewGeometryService.IsBaseAppearanceGeoset(1801) || M2PreviewGeometryService.IsBaseAppearanceGeoset(2401) || M2PreviewGeometryService.IsBaseAppearanceGeoset(2701) || M2PreviewGeometryService.IsBaseAppearanceGeoset(3201))
    throw new InvalidOperationException("Base character geoset policy enabled hair without a DBC selection or enabled equipment/customization-only geometry.");
if (previewGeometry.Vertices.Count != 3 || previewGeometry.TriangleIndices.Count != 6 || previewGeometry.TotalTriangleIndices != 9 || previewGeometry.Submeshes.Count != 3 || previewGeometry.Submeshes.Count(section => section.Visible) != 2 || !previewGeometry.Submeshes.Single(section => section.GeosetId == 702).Visible ||
    allGeosetGeometry.TriangleIndices.Count != 9 || allGeosetGeometry.Submeshes.Count(section => section.Visible) != 3 || previewGeometry.Minimum.X != -1f || previewGeometry.Maximum.Z != 1f || previewGeometry.TextureSlots.Count != 1 || previewGeometry.TextureSlots[0].EmbeddedPath != embeddedFixturePath || previewGeometry.TextureSlots[0].Flags != 3 ||
    previewGeometry.MaterialUnits.Count != 1 || previewGeometry.MaterialUnits[0].SubmeshIndex != 0 || previewGeometry.MaterialUnits[0].TextureDefinitionIndex != 0 || previewGeometry.Batches.Count != 2 || previewGeometry.Batches[0].TextureDefinitionIndex != 0 ||
    selectedHairGeometry.TriangleIndices.Count != 9 || selectedHairGeometry.Submeshes.Count(section => section.Visible) != 3 || selectedHairGeometry.GeosetSelection?.GroupVariants[0] != 2 ||
    nakedGeometry.TriangleIndices.Count != 6 || nakedGeometry.Submeshes.Count(section => section.Visible) != 2 || nakedGeometry.GeosetSelection?.GroupVariants[0] != 0)
    throw new InvalidOperationException("Native M2/SKIN preview geometry parsing failed.");
var bloodElfFemale = CharacterAppearanceService.Infer(@"Character\BloodElf\Female", "BloodElfFemale.M2");
if (bloodElfFemale is null || bloodElfFemale.RaceId != 10 || bloodElfFemale.SexId != 1) throw new InvalidOperationException("Character appearance inference did not distinguish Blood Elf female from male.");
var bloodElfSkins = CharacterAppearanceService.LoadBaseSkins(Path.Combine(args[1], "CharSections.dbc"), bloodElfFemale);
if (bloodElfSkins.Count < 10 || bloodElfSkins[0].ColorIndex != 0 || !bloodElfSkins[0].TexturePath.EndsWith(@"BloodElfFemaleSkin00_00.blp", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("CharSections base-skin discovery did not expose the real Blood Elf female appearance records.");
var bloodElfSections = CharacterAppearanceService.LoadSections(Path.Combine(args[1], "CharSections.dbc"), bloodElfFemale);
if (!bloodElfSections.Any(section => section.Kind == CharacterSectionKind.Face && section.Texture0 is not null && section.Texture1 is not null) || !bloodElfSections.Any(section => section.Kind == CharacterSectionKind.Hair && section.Texture1 is not null && section.Texture2 is not null) || !bloodElfSections.Any(section => section.Kind == CharacterSectionKind.Underwear))
    throw new InvalidOperationException("CharSections full appearance discovery omitted face, hair, or underwear component layers.");
var appearanceLibrary = Path.Combine(Path.GetTempPath(), $"crucible-appearance-library-{Guid.NewGuid():N}"); var appearanceContent = Path.Combine(appearanceLibrary,"Archives","Content");
var selectedSkinRecord=bloodElfSections.First(section=>section.Kind==CharacterSectionKind.Skin&&section.Texture0 is not null);var selectedFaceRecord=bloodElfSections.First(section=>section.Kind==CharacterSectionKind.Face&&section.ColorIndex==selectedSkinRecord.ColorIndex);var selectedFacialRecord=bloodElfSections.FirstOrDefault(section=>section.Kind==CharacterSectionKind.FacialHair);var selectedHairRecord=bloodElfSections.First(section=>section.Kind==CharacterSectionKind.Hair);var selectedUnderwearRecord=bloodElfSections.FirstOrDefault(section=>section.Kind==CharacterSectionKind.Underwear&&section.ColorIndex==selectedSkinRecord.ColorIndex);
var appearancePixels=new byte[256*256*4];for(var offset=0;offset<appearancePixels.Length;offset+=4){appearancePixels[offset]=64;appearancePixels[offset+1]=96;appearancePixels[offset+2]=128;appearancePixels[offset+3]=255;}
var appearanceBlp=Path.Combine(appearanceLibrary,"fixture.blp");Directory.CreateDirectory(appearanceLibrary);BlpTextureService.EncodeBlp2(new(256,256,appearancePixels),appearanceBlp,new(BlpOutputFormat.Dxt1,false,BlpOutputQuality.Fast));
var appearancePaths=new[]{selectedSkinRecord.Texture0,selectedFaceRecord.Texture0,selectedFaceRecord.Texture1,selectedFacialRecord?.Texture0,selectedFacialRecord?.Texture1,selectedHairRecord.Texture0,selectedHairRecord.Texture1,selectedHairRecord.Texture2,selectedUnderwearRecord?.Texture0,selectedUnderwearRecord?.Texture1}.Where(path=>!string.IsNullOrWhiteSpace(path)).Select(path=>path!).Distinct(StringComparer.OrdinalIgnoreCase);
foreach(var clientPath in appearancePaths){var output=Path.Combine(appearanceContent,Path.GetDirectoryName(clientPath)!,"fixture-source",Path.GetFileName(clientPath));Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.Copy(appearanceBlp,output);}
var appearanceIndex=AssetComparisonService.BuildIndex(appearanceLibrary);var appearancePlan=CharacterAppearancePreviewService.Build(appearanceIndex,args[1],bloodElfFemale,selectedSkinRecord.Id,selectedFaceRecord.Id,selectedFacialRecord?.Id,selectedHairRecord.Id);
var composedAppearance=CharacterAppearancePreviewService.Compose(appearanceIndex,appearancePlan);
if(appearancePlan.SelectedSource?.Provenance!="fixture-source"||appearancePlan.SelectedFace?.Id!=selectedFaceRecord.Id||appearancePlan.SelectedHair?.Id!=selectedHairRecord.Id||composedAppearance.Body.Width!=256||composedAppearance.Hair is null||composedAppearance.Missing.Count!=0)
    throw new InvalidOperationException("Reusable character appearance planning did not preserve explicit CharSections choices, provenance, body composition, or hair binding.");
Directory.Delete(appearanceLibrary,true);
var bloodElfGeosets = CharacterAppearanceService.ResolveGeosets(args[1], bloodElfFemale, 1, 0);
if (bloodElfGeosets.Hair?.GeosetId != 3 || bloodElfGeosets.Hair.VariationIndex != 1 || bloodElfGeosets.FacialHair?.Variants.Count != 5 || bloodElfGeosets.GroupVariants[0] != 3 || bloodElfGeosets.GroupVariants[17] != 2 || bloodElfGeosets.Warnings.Count != 0)
    throw new InvalidOperationException("WotLK character appearance did not resolve the selected hair/facial style into exact CharHairGeosets and CharacterFacialHairStyles groups.");
var atlasPixels = Enumerable.Repeat((byte)255, 256 * 256 * 4).ToArray(); var upperPixels = new byte[128 * 32 * 4];
for (var offset = 0; offset < upperPixels.Length; offset += 4) { upperPixels[offset] = 12; upperPixels[offset + 1] = 34; upperPixels[offset + 2] = 56; upperPixels[offset + 3] = 255; }
var composedAtlas = CharacterTextureComposer.Compose(new(256, 256, atlasPixels), [new(new(128, 32, upperPixels), CharacterTextureRegion.FaceUpper)]);
var insideUpper = (160 * 256) * 4; var outsideUpper = (159 * 256) * 4;
if (composedAtlas.Pixels[insideUpper] != 12 || composedAtlas.Pixels[insideUpper + 1] != 34 || composedAtlas.Pixels[insideUpper + 2] != 56 || composedAtlas.Pixels[outsideUpper] != 255)
    throw new InvalidOperationException("Native character atlas composition did not constrain the face-upper layer to its verified Wrath region.");
var equipmentRegions = new (int Slot, int X, int Y)[] { (0,0,0),(1,0,64),(2,0,128),(3,128,0),(4,128,64),(5,128,96),(6,128,160),(7,128,224) };
foreach (var (slot, x, y) in equipmentRegions)
{
    var component = new byte[] { 91, 73, 55, 255 }; var empty = new byte[256 * 256 * 4];
    var equipmentAtlas = CharacterTextureComposer.Compose(new(256,256,empty), [new(new(1,1,component), ItemEquipmentPreviewService.RegionForSlot(slot))]);
    var inside = (y * 256 + x) * 4;
    if (equipmentAtlas.Pixels[inside] != 91 || equipmentAtlas.Pixels[inside + 1] != 73 || equipmentAtlas.Pixels[inside + 2] != 55)
        throw new InvalidOperationException($"Wear texture slot {slot} did not map to the verified Wrath atlas origin {x},{y}.");
}
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
if (nativeWorkspace.FormatVersion != 2 || nativeWorkspace.CompatibleAssets != 1 || nativeWorkspace.ConversionRequired != 1 || !File.Exists(Path.Combine(conversionWorkspacePath, "conversion-report.json")) ||
    Directory.EnumerateFiles(Path.Combine(conversionWorkspacePath, "source"), "fixture.m2", SearchOption.AllDirectories).Count() != 1 ||
    !NativeAssetConversionService.ResolveSnapshotPath(nativeWorkspace, nativeWorkspace.Assets.Single(asset => asset.Path == wotlkModel)).Contains($"{Path.DirectorySeparatorChar}asset{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    throw new InvalidOperationException("Native conversion workspace did not preserve immutable hashed inputs and report compatibility.");
var reopenedNativeWorkspace = NativeAssetConversionService.LoadWorkspace(Path.Combine(conversionWorkspacePath, "conversion-report.json"));
var movedConversionWorkspacePath = conversionWorkspacePath + "-moved"; Directory.Move(conversionWorkspacePath, movedConversionWorkspacePath);
var movedNativeWorkspace = NativeAssetConversionService.LoadWorkspace(movedConversionWorkspacePath);
if (reopenedNativeWorkspace.Assets.Count != 2 || movedNativeWorkspace.RootPath != Path.GetFullPath(movedConversionWorkspacePath))
    throw new InvalidOperationException("Native conversion reports could not be reopened or rebound after a workspace move.");
var tamperedSnapshot = NativeAssetConversionService.ResolveSnapshotPath(movedNativeWorkspace, movedNativeWorkspace.Assets.Single(asset => asset.Path == modernModel)); File.AppendAllText(tamperedSnapshot, "tampered");
try { _ = NativeAssetConversionService.LoadWorkspace(movedConversionWorkspacePath); throw new InvalidOperationException("Native conversion workspace accepted a tampered immutable snapshot."); }
catch (InvalidDataException) { }
Directory.Delete(movedConversionWorkspacePath, true);
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
var unrelatedModels = AssetComparisonService.GetRelevantModels(comparisonIndex, @"Character\Human\Female\Hair");
if (comparisonIndex.TotalPngFiles != 9 || matchingDirectory.ProvenanceSources != 3 || comparisonEntries.Count != 4 || comparisonEntries.Select(entry => entry.FileName).Distinct().Count() != 4 ||
    looseOnlyEntries.Count != 1 || looseOnlyEntries[0].Provenance != "ExtendedSkins" || exactDuplicates.Count != 1 || exactDuplicates[0].Entries.Count != 2 || exactDuplicates[0].RecoverableBytes != 3 || comparisonModels.Count != 1 || comparisonModels[0].Compatibility != AssetModelCompatibility.Ready || ancestorModels.DiscoveryScope != @"Character\BloodElf\Female" || ancestorModels.Models.Count != 1 || unrelatedModels.Models.Count != 0)
    throw new InvalidOperationException("Directory-first visual comparison did not merge matching provenance sources after Loose consolidation.");
var definitivePath = DefinitiveAssetProjectService.DefaultPath(comparisonRoot); var definitive = DefinitiveAssetProjectService.LoadOrCreate(definitivePath, comparisonRoot);
definitive = DefinitiveAssetProjectService.RecordTexture(definitivePath, definitive, comparisonEntries[0], AssetDecision.Keeper, "Race", "fixture keeper");
var dependencyGraph = AssetDependencyGraphService.AnalyzeModel(comparisonIndex, comparisonModels[0]);
if (dependencyGraph.Blocking.Count != 0 || !dependencyGraph.Resolved.Any(dependency => dependency.Kind == "embedded-texture" && dependency.ClientPath == embeddedFixturePath))
    throw new InvalidOperationException("Model dependency closure did not resolve an embedded texture from the same provenance layer.");
var dependencySource="graph-source";var dependencyContent=Path.Combine(comparisonRoot,"Archives","Content");
var graphTerrain=WriteGraphAsset(dependencyContent,dependencySource,@"Textures\Graph\Terrain.blp",[1,2,3]);
WriteGraphAsset(dependencyContent,dependencySource,@"Textures\Graph\Wmo.blp",[4,5,6]);WriteGraphAsset(dependencyContent,dependencySource,@"Textures\Graph\Model.blp",[7,8,9]);
var graphModelBytes=new byte[0x200];System.Text.Encoding.ASCII.GetBytes("MD20").CopyTo(graphModelBytes,0);BitConverter.GetBytes((uint)264).CopyTo(graphModelBytes,4);BitConverter.GetBytes((uint)1).CopyTo(graphModelBytes,0x44);BitConverter.GetBytes((uint)1).CopyTo(graphModelBytes,0x50);BitConverter.GetBytes((uint)0x100).CopyTo(graphModelBytes,0x54);
var graphModelTexture=System.Text.Encoding.UTF8.GetBytes("Textures\\Graph\\Model.blp\0");BitConverter.GetBytes((uint)graphModelTexture.Length).CopyTo(graphModelBytes,0x108);BitConverter.GetBytes((uint)0x110).CopyTo(graphModelBytes,0x10C);graphModelTexture.CopyTo(graphModelBytes,0x110);
WriteGraphAsset(dependencyContent,dependencySource,@"World\Model\Graph\Doodad.m2",graphModelBytes);WriteGraphAsset(dependencyContent,dependencySource,@"World\Model\Graph\Doodad00.skin",System.Text.Encoding.ASCII.GetBytes("SKIN"));
WriteGraphAsset(dependencyContent,dependencySource,@"World\Wmo\Graph\House_000.wmo",GraphChunks(("MVER",BitConverter.GetBytes((uint)17)),("MOGP",new byte[4])));
var graphMohd=new byte[64];BitConverter.GetBytes((uint)1).CopyTo(graphMohd,4);WriteGraphAsset(dependencyContent,dependencySource,@"World\Wmo\Graph\House.wmo",GraphChunks(("MVER",BitConverter.GetBytes((uint)17)),("MOHD",graphMohd),("MOTX",GraphStrings(@"Textures\Graph\Wmo.blp")),("MODN",GraphStrings(@"World\Model\Graph\Doodad.m2"))));
var graphAdt=WriteGraphAsset(dependencyContent,dependencySource,@"World\Maps\Graph\Graph_1_1.adt",GraphChunks(("MVER",BitConverter.GetBytes((uint)18)),("MTEX",GraphStrings(@"Textures\Graph\Terrain.blp")),("MMDX",GraphStrings(@"World\Model\Graph\Doodad.m2")),("MWMO",GraphStrings(@"World\Wmo\Graph\House.wmo"))));
var recursiveIndex=AssetComparisonService.BuildIndex(comparisonRoot);var recursiveGraph=ClientAssetDependencyService.Analyze(recursiveIndex,graphAdt);
if(recursiveGraph.Blocking.Count!=0||recursiveGraph.PatchEntries.Count!=8||!recursiveGraph.Nodes.Any(node=>node.Kind=="wmo-group")||!recursiveGraph.Nodes.Any(node=>node.Kind=="wmo-doodad-model")||!recursiveGraph.Nodes.Any(node=>node.Kind=="embedded-texture")||!recursiveGraph.Nodes.Any(node=>node.Kind=="terrain-texture"))
    throw new InvalidOperationException($"Recursive ADT/WMO/M2/SKIN/BLP dependency closure was incomplete: files={recursiveGraph.PatchEntries.Count}, blocking={recursiveGraph.Blocking.Count}.");
var graphManifest=Path.Combine(assetFixture,"graph.crucible-patch.json");PatchManifestService.Save(graphManifest,"graph fixture","patch-graph.MPQ",recursiveGraph.PatchEntries,policy:new(ExpectedEntryCount:recursiveGraph.PatchEntries.Count));if(!PatchManifestService.Validate(PatchManifestService.Load(graphManifest)).Passed)throw new InvalidOperationException("Dependency closure did not produce a valid portable patch manifest.");
var otherTerrain=Path.Combine(dependencyContent,"Textures","Graph","other-source","Terrain.blp");Directory.CreateDirectory(Path.GetDirectoryName(otherTerrain)!);File.Move(graphTerrain,otherTerrain);var conflictingGraph=ClientAssetDependencyService.Analyze(recursiveIndex,graphAdt);var explicitGraph=ClientAssetDependencyService.Analyze(recursiveIndex,ClientAssetDependencyService.InferLocation(recursiveIndex,graphAdt),new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){[@"Textures\Graph\Terrain.blp"]=otherTerrain});File.Move(otherTerrain,graphTerrain);
if(!conflictingGraph.Blocking.Any(node=>node.State==ClientAssetDependencyState.CrossSourceConflict&&node.ClientPath.Equals(@"Textures\Graph\Terrain.blp",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Recursive dependency closure silently mixed a required asset from another provenance layer.");
if(explicitGraph.Blocking.Count!=0||!explicitGraph.Nodes.Any(node=>node.ClientPath.Equals(@"Textures\Graph\Terrain.blp",StringComparison.OrdinalIgnoreCase)&&node.Provenance=="other-source"&&node.State==ClientAssetDependencyState.Resolved))throw new InvalidOperationException("An explicit cross-provenance dependency resolution did not produce a complete auditable graph.");
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

var aggregateLibrary = Path.Combine(assetFixture, "aggregate-sidecar"); var aggregateContent = Path.Combine(aggregateLibrary, "Archives", "Content");
var aggregatePng = Path.Combine(aggregateContent, "Character", "CacheTest", "patch-one", "one.png");
var aggregateModel = Path.Combine(aggregateContent, "Creature", "ModelOnly", "patch-model", "geometry.m2");
var aggregateSkin = Path.Combine(aggregateContent, "Creature", "ModelOnly", "patch-model", "geometry00.skin");
Directory.CreateDirectory(Path.GetDirectoryName(aggregatePng)!); Directory.CreateDirectory(Path.GetDirectoryName(aggregateModel)!);
File.WriteAllText(aggregatePng, "png"); File.Copy(geometryModelPath, aggregateModel); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), aggregateSkin);
File.WriteAllText(Path.Combine(aggregateLibrary, "asset-library-plan.json"), System.Text.Json.JsonSerializer.Serialize(new BulkAssetLibraryPlan(aggregateLibrary, aggregateLibrary, 1, DateTimeOffset.UtcNow, 0, [])));
var aggregateCatalog = BulkAssetLibraryService.RebuildCatalog(aggregateLibrary); var aggregateSidecar = Path.Combine(aggregateLibrary, AssetComparisonService.AggregateSidecarFileName);
var validAggregateIndex = AssetComparisonService.BuildIndex(aggregateLibrary);
var modelOnlyDirectory = validAggregateIndex.Directories.SingleOrDefault(directory => directory.LogicalPath == @"Creature\ModelOnly");
if (!File.Exists(aggregateSidecar) || validAggregateIndex.Source != AssetComparisonIndexSource.Sidecar || modelOnlyDirectory is null || modelOnlyDirectory.PngFiles != 0 || modelOnlyDirectory.M2Files != 1 || modelOnlyDirectory.SkinFiles != 1)
    throw new InvalidOperationException("A valid aggregate sidecar was not loaded or its model-only directory disappeared from navigation.");

var stalePng = Path.Combine(aggregateContent, "Character", "CacheStale", "patch-two", "stale.png"); Directory.CreateDirectory(Path.GetDirectoryName(stalePng)!); File.WriteAllText(stalePng, "stale");
File.AppendAllText(aggregateCatalog, $"Characters,PNG,patch-two,{Path.GetRelativePath(aggregateLibrary, stalePng)},{new FileInfo(stalePng).Length}{Environment.NewLine}");
var staleFallbackIndex = AssetComparisonService.BuildIndex(aggregateLibrary);
if (staleFallbackIndex.Source != AssetComparisonIndexSource.Catalog || !staleFallbackIndex.Directories.Any(directory => directory.LogicalPath == @"Character\CacheStale") || Directory.EnumerateFiles(aggregateLibrary, "*.tmp", SearchOption.TopDirectoryOnly).Any())
    throw new InvalidOperationException("A stale aggregate sidecar was trusted, or its atomic catalog fallback/cache refresh failed.");
if (AssetComparisonService.BuildIndex(aggregateLibrary).Source != AssetComparisonIndexSource.Sidecar)
    throw new InvalidOperationException("The aggregate cache rebuilt from a stale sidecar was not reusable on the next open.");

File.WriteAllText(aggregateSidecar, "{ definitely-not-json");
var corruptSidecarFallbackIndex = AssetComparisonService.BuildIndex(aggregateLibrary);
if (corruptSidecarFallbackIndex.Source != AssetComparisonIndexSource.Catalog || !corruptSidecarFallbackIndex.Directories.Any(directory => directory.LogicalPath == @"Creature\ModelOnly"))
    throw new InvalidOperationException("A corrupt aggregate sidecar did not fall back to the catalog and preserve model-only paths.");
File.WriteAllText(aggregateCatalog, $@"category,format,source,relative_path,bytes{Environment.NewLine}Other,PNG,patch-bad,Archives\Content\..\Outside\patch-bad\escape.png,1{Environment.NewLine}");
var unsafeCatalogFallbackIndex = AssetComparisonService.BuildIndex(aggregateLibrary);
if (unsafeCatalogFallbackIndex.Source != AssetComparisonIndexSource.FileSystem || unsafeCatalogFallbackIndex.Directories.Any(directory => directory.LogicalPath.Split('\\', '/').Contains("..")))
    throw new InvalidOperationException("An unsafe logical path from a tampered catalog was exposed or cached instead of falling back to the filesystem.");
File.WriteAllText(aggregateCatalog, "not,a,valid,catalog");
var corruptCatalogFallbackIndex = AssetComparisonService.BuildIndex(aggregateLibrary);
if (corruptCatalogFallbackIndex.Source != AssetComparisonIndexSource.FileSystem || !corruptCatalogFallbackIndex.Directories.Any(directory => directory.LogicalPath == @"Creature\ModelOnly"))
    throw new InvalidOperationException("A corrupt asset catalog did not fall back safely to the filesystem.");
using (var cancelledAggregate = new CancellationTokenSource())
{
    cancelledAggregate.Cancel();
    try { _ = AssetComparisonService.BuildIndex(aggregateLibrary, cancelledAggregate.Token); throw new InvalidOperationException("Asset aggregate indexing ignored cancellation."); }
    catch (OperationCanceledException) { }
}
File.Delete(aggregateSidecar); Directory.CreateDirectory(aggregateSidecar);
var catalogWithBlockedSidecar = BulkAssetLibraryService.RebuildCatalog(aggregateLibrary);
if (!File.Exists(catalogWithBlockedSidecar) || Directory.EnumerateFiles(aggregateLibrary, $"{AssetComparisonService.AggregateSidecarFileName}.*.tmp", SearchOption.TopDirectoryOnly).Any())
    throw new InvalidOperationException("A nonessential sidecar write failure poisoned a committed catalog rebuild or left temporary files behind.");

var targetClientFixture = Path.Combine(Path.GetTempPath(), $"crucible-target-client-{Guid.NewGuid():N}"); var targetClientData = Path.Combine(targetClientFixture, "Data"); Directory.CreateDirectory(targetClientData);
Directory.CreateDirectory(Path.Combine(targetClientFixture, "WTF")); File.WriteAllText(Path.Combine(targetClientFixture, "WTF", "Config.wtf"), "SET locale \"enUS\"");
var targetCommon = Path.Combine(targetClientData, "common.MPQ"); var targetPatch3 = Path.Combine(targetClientData, "patch-3.MPQ");
var graphWmoTexturePath = Path.Combine(dependencyContent, "Textures", "Graph", dependencySource, "Wmo.blp");
var targetDifferentWmo = Path.Combine(targetClientFixture, "different-wmo.blp"); File.WriteAllBytes(targetDifferentWmo, [99, 98, 97, 96]);
var targetPatchService = new PatchArchiveService();
targetPatchService.Create(targetCommon, [new(graphTerrain, @"Textures\Graph\Terrain.blp"), new(graphWmoTexturePath, @"Textures\Graph\Wmo.blp")]);
targetPatchService.Create(targetPatch3, [new(targetDifferentWmo, @"Textures\Graph\Wmo.blp")]);
var targetIndexDirectory = Path.Combine(targetClientFixture, "target-index"); var targetIndexer = new ClientArchiveIndexService(); targetIndexer.Build(targetClientFixture, targetIndexDirectory, false);
var targetCatalog = ClientEffectiveAssetCatalog.Load(targetIndexDirectory); var effectiveWmo = targetCatalog.Resolve(@"Textures\Graph\Wmo.blp");
if (effectiveWmo.State != ClientEffectiveAssetState.Effective || effectiveWmo.Effective?.ArchiveRelativePath != @"Data\patch-3.MPQ")
    throw new InvalidOperationException("Effective target-client ordering did not select the higher Wrath patch layer.");
var targetAwareGraph = ClientAssetDependencyService.Analyze(recursiveIndex, ClientAssetDependencyService.InferLocation(recursiveIndex, graphAdt), null, targetCatalog);
if (targetAwareGraph.Blocking.Count != 0 || targetAwareGraph.PatchEntries.Count != 7 ||
    !targetAwareGraph.Nodes.Any(node => node.ClientPath.Equals(@"Textures\Graph\Terrain.blp", StringComparison.OrdinalIgnoreCase) && node.State == ClientAssetDependencyState.TargetInherited) ||
    !targetAwareGraph.Nodes.Any(node => node.ClientPath.Equals(@"Textures\Graph\Wmo.blp", StringComparison.OrdinalIgnoreCase) && node.State == ClientAssetDependencyState.TargetOverride))
    throw new InvalidOperationException($"Target-aware closure did not omit exact bytes and retain a different-byte override: patch={targetAwareGraph.PatchEntries.Count}, inherited={targetAwareGraph.Inherited.Count}, blocking={targetAwareGraph.Blocking.Count}.");
var targetManifestPath = Path.Combine(targetClientFixture, "target-bound.crucible-patch.json");
PatchManifestService.Save(targetManifestPath, "target closure", "patch-target.MPQ", targetAwareGraph.PatchEntries, policy: new(ExpectedEntryCount: targetAwareGraph.PatchEntries.Count), targetClient: targetAwareGraph.TargetRequirement);
var loadedTargetManifest = PatchManifestService.Load(targetManifestPath);
if (loadedTargetManifest.FormatVersion != 4 || loadedTargetManifest.TargetClient?.InheritedAssets.Count != 1 || !PatchManifestService.Validate(loadedTargetManifest).Passed || loadedTargetManifest.TargetClient.IndexFingerprint != targetCatalog.Fingerprint)
    throw new InvalidOperationException("A minimal target-aware closure did not persist and validate its inherited path hashes and client-index fingerprint.");
var heldTerrain = graphTerrain + ".held"; File.Move(graphTerrain, heldTerrain);
try
{
    var inheritedMissingGraph = ClientAssetDependencyService.Analyze(recursiveIndex, ClientAssetDependencyService.InferLocation(recursiveIndex, graphAdt), null, targetCatalog);
    if (inheritedMissingGraph.Blocking.Count != 0 || !inheritedMissingGraph.Nodes.Any(node => node.ClientPath.Equals(@"Textures\Graph\Terrain.blp", StringComparison.OrdinalIgnoreCase) && node.State == ClientAssetDependencyState.TargetInherited && node.SourcePath is null))
        throw new InvalidOperationException("An exact effective target path did not safely satisfy a dependency missing from the processed source layer.");
}
finally { File.Move(heldTerrain, graphTerrain); }
var mysterySource = Path.Combine(targetClientFixture, "mystery-terrain.blp"); File.WriteAllBytes(mysterySource, [55, 54, 53]);
targetPatchService.Create(Path.Combine(targetClientData, "mystery.MPQ"), [new(mysterySource, @"Textures\Graph\Terrain.blp")]);
targetIndexer.Build(targetClientFixture, targetIndexDirectory, false); var ambiguousTarget = ClientEffectiveAssetCatalog.Load(targetIndexDirectory);
File.Move(graphTerrain, heldTerrain);
try
{
    var ambiguousGraph = ClientAssetDependencyService.Analyze(recursiveIndex, ClientAssetDependencyService.InferLocation(recursiveIndex, graphAdt), null, ambiguousTarget);
    if (!ambiguousGraph.Blocking.Any(node => node.ClientPath.Equals(@"Textures\Graph\Terrain.blp", StringComparison.OrdinalIgnoreCase) && node.State == ClientAssetDependencyState.TargetAmbiguous))
        throw new InvalidOperationException("A missing local dependency trusted a target path whose nonstandard archive precedence was ambiguous.");
    var explicitTargetGraph = ClientAssetDependencyService.Analyze(recursiveIndex, ClientAssetDependencyService.InferLocation(recursiveIndex, graphAdt), null, ambiguousTarget,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"Textures\Graph\Terrain.blp"] = @"Data\common.MPQ" });
    if (explicitTargetGraph.Blocking.Count != 0 || !explicitTargetGraph.Nodes.Any(node => node.ClientPath.Equals(@"Textures\Graph\Terrain.blp", StringComparison.OrdinalIgnoreCase) && node.State == ClientAssetDependencyState.TargetInherited && node.TargetArchive == @"Data\common.MPQ"))
        throw new InvalidOperationException("An explicit effective target-archive choice did not resolve and record an ambiguous inherited dependency.");
}
finally { File.Move(heldTerrain, graphTerrain); }
Directory.Delete(targetClientFixture, true);
Directory.Delete(assetFixture, true);

var targetProfiles = TargetProfileCatalog.Load(Path.Combine(Path.GetTempPath(), $"crucible-profiles-{Guid.NewGuid():N}"), Path.Combine(Path.GetTempPath(), $"crucible-app-profiles-{Guid.NewGuid():N}"));
if (targetProfiles.Count != 4 || TargetProfileCatalog.Find(targetProfiles, null).ClientBuild != 12340 ||
    targetProfiles.Single(profile => profile.ClientBuild == 15595).SupportTier != TargetSupportTier.Experimental)
    throw new InvalidOperationException("Built-in target profiles are incomplete or the verified default changed.");
var contentProjectRoot = Path.Combine(Path.GetTempPath(), $"crucible-content-project-{Guid.NewGuid():N}"); var defaultContentProject = CrucibleContentProjectService.Create(contentProjectRoot, "Fixture project");
var firstIds = CrucibleContentProjectService.ReserveIds(contentProjectRoot, ContentIdDomain.CreatureDisplayInfo, 3, 100, [100u, 102u], "fixture displays").Reservation.Values;
var secondIds = CrucibleContentProjectService.ReserveIds(contentProjectRoot, ContentIdDomain.CreatureDisplayInfo, 2, 100, [100u, 102u], "more displays").Reservation.Values;
if (defaultContentProject.TargetProfile != TargetProfileCatalog.DefaultProfileId || TargetProfileCatalog.Find(targetProfiles, defaultContentProject.TargetProfile).Id != TargetProfileCatalog.DefaultProfileId ||
    !firstIds.SequenceEqual([101u, 103u, 104u]) || !secondIds.SequenceEqual([105u, 106u]) || CrucibleContentProjectService.LoadRegistry(contentProjectRoot).Reservations.Count != 2 || !Directory.Exists(Path.Combine(contentProjectRoot, "Staging")))
    throw new InvalidOperationException("Portable content-project ID reservations collided with occupied or previously reserved IDs.");
Directory.Delete(contentProjectRoot, true);
var customProfileDirectory = Path.Combine(Path.GetTempPath(), $"crucible-custom-profile-{Guid.NewGuid():N}");
TargetProfileCatalog.SaveTemplate(Path.Combine(customProfileDirectory, "custom.json"), new("custom-9999", "Custom Test Build", "Test", 9999, "custom.xml", ClientTableFormat.Wdbc, ArchiveFormat.Mpq, TargetSupportTier.Experimental, "Fixture"));
var profilesWithCustom = TargetProfileCatalog.Load(customProfileDirectory, Path.Combine(Path.GetTempPath(), $"crucible-empty-{Guid.NewGuid():N}"));
if (profilesWithCustom.Count != 5 || profilesWithCustom.Single(profile => profile.Id == "custom-9999").ClientBuild != 9999)
    throw new InvalidOperationException("External target profile loading failed.");
Directory.Delete(customProfileDirectory, true);

var sqlTransferFixture = Path.Combine(Path.GetTempPath(), $"crucible-sql-transfer-{Guid.NewGuid():N}.csv");
var sqlTransferTable = new DatabaseTableCapability("fixture_table",
[
    new("id", "int", "int unsigned", false, null, "PRI", string.Empty, 1),
    new("name", "varchar", "varchar(255)", false, null, string.Empty, string.Empty, 2),
    new("notes", "text", "text", true, null, string.Empty, string.Empty, 3)
]);
File.WriteAllText(sqlTransferFixture, $"id,name,notes{Environment.NewLine}1,\"Quoted, name\",\\N{Environment.NewLine}2,\"Multi{Environment.NewLine}line\",text{Environment.NewLine}");
var sqlImportPlan = new SqlTransferService().AnalyzeCsv(sqlTransferFixture, sqlTransferTable);
using (var sqlReader = new StringReader(File.ReadAllText(sqlTransferFixture)))
{
    var decoded = SqlCsvCodec.ReadRows(sqlReader).ToArray();
    if (decoded.Length != 3 || decoded[1][1] != "Quoted, name" || decoded[1][2] is not null || decoded[2][1] != $"Multi{Environment.NewLine}line")
        throw new InvalidOperationException("SQL CSV codec did not preserve commas, quotes, multiline text, or NULL values.");
}
if (!sqlImportPlan.CanApply || sqlImportPlan.Rows != 2 || !sqlImportPlan.Columns.SequenceEqual(["id", "name", "notes"]))
    throw new InvalidOperationException("SQL CSV dry-run did not validate the complete import shape.");
File.WriteAllText(sqlTransferFixture, $"id,unknown{Environment.NewLine}1,nope{Environment.NewLine}");
if (new SqlTransferService().AnalyzeCsv(sqlTransferFixture, sqlTransferTable).CanApply)
    throw new InvalidOperationException("SQL CSV dry-run accepted an unknown column or omitted a required field.");
File.Delete(sqlTransferFixture);

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
File.WriteAllText(Path.Combine(serverFixture, "Start-Server.ps1"), """
    $distro = 'Fixture-Linux'
    Start-Process -FilePath 'wsl.exe' -ArgumentList @('-d', $distro, '--', '/srv/acore/bin/authserver', '--config', '/srv/acore/etc/authserver.conf')
    Start-Process -FilePath 'wsl.exe' -ArgumentList @('-d', $distro, '--', '/srv/acore/bin/worldserver', '--config', '/srv/acore/etc/worldserver.conf')
    """);
var wslLauncher = ServerWorkspaceDetector.DetectWslLauncher(serverFixture);
if (wslLauncher is null || wslLauncher.Value.Distribution != "Fixture-Linux" || wslLauncher.Value.WorldExecutable != "/srv/acore/bin/worldserver" ||
    wslLauncher.Value.AuthExecutable != "/srv/acore/bin/authserver" || wslLauncher.Value.ConfigPath != "/srv/acore/etc/worldserver.conf" || wslLauncher.Value.AuthConfigPath != "/srv/acore/etc/authserver.conf")
    throw new InvalidOperationException("Native WSL lifecycle path detection failed.");
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
if (ItemCatalogService.IsDirectLootItem(17, 1_072_025) || !ItemCatalogService.IsDirectLootItem(17, 0) ||
    ItemCatalogService.IsLinkedQuestReward(7561, new HashSet<uint> { 7561 }, new HashSet<uint>()) || !ItemCatalogService.IsLinkedQuestReward(7561, new HashSet<uint> { 7561 }, new HashSet<uint> { 7561 }) ||
    ItemCatalogService.IsUsableQuestReward(7561, new HashSet<uint> { 7561 }, new HashSet<uint> { 7561 }, new HashSet<uint> { 7561 }) ||
    !ItemCatalogService.IsUsableQuestReward(7561, new HashSet<uint> { 7561 }, new HashSet<uint> { 7561 }, new HashSet<uint>()))
    throw new InvalidOperationException("Item acquisition evidence confused loot references or unlinked quest rewards with direct player acquisition.");
var startingItems = ItemCatalogService.ReadCharStartOutfitItems(Path.Combine(args[1], "CharStartOutfit.dbc"));
if (!startingItems.Contains(6948) || startingItems.Contains(17) || startingItems.Contains(17802))
    throw new InvalidOperationException("CharStartOutfit DBC acquisition coverage did not distinguish starting equipment from cut/developer items.");
var spellCreation = ItemCatalogService.ReadSpellCreationGraph(Path.Combine(args[1], "Spell.dbc"));
if (!spellCreation.TryGetValue(597, out var conjureFood) || !conjureFood.CreatedItems.Contains(1113u) || spellCreation.Count < 1_000)
    throw new InvalidOperationException("Spell.dbc acquisition coverage did not map reachable create-item effects such as Conjured Bread.");
if (!SqlWorkspaceService.IsReadOnlyStatement("-- reviewed\nSELECT * FROM item_template") || SqlWorkspaceService.IsReadOnlyStatement("/* not read-only */ UPDATE item_template SET name='bad'"))
    throw new InvalidOperationException("SQL Studio read-only routing could execute a write through the immediate query path.");
var joinSource = new DatabaseTableCapability("item_template", ItemColumns("entry", "name"));
var joinTarget = new DatabaseTableCapability("npc_vendor", ItemColumns("entry", "item"));
var joinRelation = new DatabaseRelationCapability("fixture_item_vendor", "item_template", "entry", "npc_vendor", "item", false, "Fixture relation");
var joinSql = SqlAdministrationService.BuildJoinSql(joinRelation, joinSource, joinTarget, "left", 25);
if (!joinSql.Contains("source.`entry` AS `source__entry`", StringComparison.Ordinal) ||
    !joinSql.Contains("target.`entry` AS `target__entry`", StringComparison.Ordinal) ||
    !joinSql.Contains("LEFT JOIN `npc_vendor`", StringComparison.Ordinal) ||
    !joinSql.EndsWith("LIMIT 25;", StringComparison.Ordinal))
    throw new InvalidOperationException("Visual SQL joins no longer produce exact, uniquely aliased read-only output.");
try { _ = SqlAdministrationService.BuildJoinSql(joinRelation, joinSource, joinTarget, "CROSS", 25); throw new InvalidOperationException("An unsupported visual join type was accepted."); }
catch (ArgumentException) { }
var createIndexSql = SqlAdministrationService.BuildCreateIndexSql(joinSource, "crucible_name", ["name"], true);
if (createIndexSql != "CREATE UNIQUE INDEX `crucible_name` ON `item_template` (`name`);")
    throw new InvalidOperationException("Reviewed index SQL generation changed unexpectedly.");
try { _ = SqlAdministrationService.BuildCreateIndexSql(joinSource, "crucible_bad", ["missing"], false); throw new InvalidOperationException("Index SQL accepted an unknown table column."); }
catch (ArgumentException) { }
try { _ = SqlAdministrationService.BuildDropIndexSql(joinSource, "PRIMARY"); throw new InvalidOperationException("Ordinary index administration could drop a primary key."); }
catch (InvalidOperationException exception) when (exception.Message.Contains("primary", StringComparison.OrdinalIgnoreCase)) { }
var supportedPrivileges = new[] { new SqlPrivilegeInfo("Select", "Tables", "read rows"), new SqlPrivilegeInfo("Insert", "Tables", "insert rows"), new SqlPrivilegeInfo("Show View", "Tables", "show views"), new SqlPrivilegeInfo("Grant Option", "Databases,Tables", "grant privileges") };
var createUserSql = SqlAdministrationService.BuildCreateUserSql("crucible'editor", "localhost", true);
if (!createUserSql.Contains("'crucible''editor'@'localhost'", StringComparison.Ordinal) || !createUserSql.Contains("@crucible_new_password", StringComparison.Ordinal) || createUserSql.Contains("<password", StringComparison.Ordinal))
    throw new InvalidOperationException("Account creation SQL stopped quoting account identities or parameterizing the new password.");
var redactedUserSql = SqlAdministrationService.RedactPasswordSql(createUserSql);
if (redactedUserSql.Contains("@crucible_new_password", StringComparison.Ordinal) || !redactedUserSql.Contains("<password supplied in memory>", StringComparison.Ordinal))
    throw new InvalidOperationException("Account-plan display did not redact the password parameter clearly.");
var grantSql = SqlAdministrationService.BuildGrantSql("editor", "localhost", "acore_world", "item_template", false, ["select", "insert"], supportedPrivileges, true);
if (grantSql != "GRANT SELECT, INSERT ON `acore_world`.`item_template` TO 'editor'@'localhost' WITH GRANT OPTION;")
    throw new InvalidOperationException("Exact table-scoped privilege planning changed unexpectedly.");
var globalRevokeSql = SqlAdministrationService.BuildRevokeSql("editor", "%", "ignored", null, true, ["SHOW_VIEW"], supportedPrivileges);
if (globalRevokeSql != "REVOKE SHOW VIEW ON *.* FROM 'editor'@'%';") throw new InvalidOperationException("Global privilege revocation planning failed.");
try { _ = SqlAdministrationService.BuildGrantSql("editor", "localhost", "acore_world", null, false, ["FILE"], supportedPrivileges, false); throw new InvalidOperationException("Privilege planning accepted a capability the server did not report."); }
catch (ArgumentException exception) when (exception.Message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)) { }
try { _ = SqlAdministrationService.BuildGrantSql("editor", "localhost", "acore_world", "item_template", true, ["SELECT"], supportedPrivileges, false); throw new InvalidOperationException("Privilege planning combined a table with global scope."); }
catch (ArgumentException) { }
try { _ = SqlAdministrationService.BuildDropUserSql("editor; DROP DATABASE acore_world", "localhost"); throw new InvalidOperationException("Account planning accepted a statement delimiter."); }
catch (ArgumentException) { }
try { _ = SqlAdministrationService.BuildDropUserSql("editor\\", "localhost"); throw new InvalidOperationException("Account planning accepted a backslash that could alter MySQL string parsing."); }
catch (ArgumentException) { }

var modernCreatureTable = new DatabaseTableCapability("creature_template", ItemColumns("entry", "name", "subname", "minlevel", "maxlevel", "faction", "npcflag", "rank", "type", "family", "unit_class", "speed_walk", "speed_run", "HealthModifier", "ManaModifier", "ArmorModifier", "DamageModifier", "BaseAttackTime", "RangeAttackTime", "unit_flags", "unit_flags2", "dynamicflags", "type_flags", "lootid", "pickpocketloot", "skinloot", "mingold", "maxgold", "AIName", "ScriptName", "RegenHealth", "VerifiedBuild"));
var modernCreatureModelTable = new DatabaseTableCapability("creature_template_model", ItemColumns("CreatureID", "Idx", "CreatureDisplayID", "DisplayScale", "Probability", "VerifiedBuild"));
var modernVendorTable = new DatabaseTableCapability("npc_vendor", ItemColumns("entry", "slot", "item", "maxcount", "incrtime", "ExtendedCost", "VerifiedBuild"));
var modernLootTable = new DatabaseTableCapability("creature_loot_template", ItemColumns("Entry", "Item", "Reference", "Chance", "QuestRequired", "LootMode", "GroupId", "MinCount", "MaxCount", "Comment"));
var modernCreatureCapabilities = new DatabaseCapabilities("fixture", "world", new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { [modernCreatureTable.Name] = modernCreatureTable, [modernCreatureModelTable.Name] = modernCreatureModelTable, [modernVendorTable.Name] = modernVendorTable, [modernLootTable.Name] = modernLootTable });
var creatureDraft = new CreatureTemplateDraft(900100, "Crucible's Guardian", "Layer test", [1234, 5678], 80, 83, 35, 1, 1, 7, 0, 1, 1.1f, 1f, 1.14286f, 2f, 1f, 1.5f, 3f, 2000, 2000, 0, 0, 0, 0, 900100, 0, 0, 10, 50, "SmartAI", "", true,
    [new(-1, 6948, 0, 0, 0)], [new(900100, 6948, 0, 25, false, 1, 0, 1, 2, "Crucible loot")]);
var modernCreaturePlan = CreatureTemplateAdapter.CreatePlan(creatureDraft, modernCreatureCapabilities);
if (modernCreaturePlan.Rows.Count != 5 || modernCreaturePlan.Rows[1].Values["CreatureDisplayID"] is not 1234u || modernCreaturePlan.Rows[2].Values["CreatureDisplayID"] is not 5678u ||
    modernCreaturePlan.Rows[3].Table != "npc_vendor" || modernCreaturePlan.Rows[4].Table != "creature_loot_template" || !modernCreaturePlan.PreviewSql().Contains("Crucible''s Guardian") || !modernCreaturePlan.PreviewSql().Contains("Crucible loot"))
    throw new InvalidOperationException("Modern schema-aware creature/model/vendor/loot planning failed.");
var legacyCreatureTable = new DatabaseTableCapability("creature_template", ItemColumns("entry", "name", "subname", "minlevel", "maxlevel", "faction", "modelid1", "modelid2", "modelid3", "modelid4"));
var legacyVendorTable = new DatabaseTableCapability("npc_vendor", ItemColumns("entry", "item", "maxcount", "incrtime"));
var legacyLootTable = new DatabaseTableCapability("creature_loot_template", ItemColumns("Entry", "Item", "Chance", "MinCount", "MaxCount"));
var legacyCreatureCapabilities = new DatabaseCapabilities("fixture", "world", new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { [legacyCreatureTable.Name] = legacyCreatureTable, [legacyVendorTable.Name] = legacyVendorTable, [legacyLootTable.Name] = legacyLootTable });
var legacyCreaturePlan = CreatureTemplateAdapter.CreatePlan(creatureDraft, legacyCreatureCapabilities);
if (legacyCreaturePlan.Rows.Count != 3 || legacyCreaturePlan.Rows[0].Values["modelid1"] is not 1234u || legacyCreaturePlan.Rows[0].Values["modelid2"] is not 5678u || legacyCreaturePlan.Rows[1].Key.Count != 2 || legacyCreaturePlan.Rows[2].Key.Count != 2)
    throw new InvalidOperationException("Legacy creature/template/vendor/loot adaptation failed.");

if (GameObjectTypeCatalog.All.Count != 36 || GameObjectTypeCatalog.Find(3).Field(1).Name != "lootId" || GameObjectTypeCatalog.Find(10).Field(19).Name != "gossipID" || GameObjectTypeCatalog.Find(33).Field(22).Name != "damageEvent")
    throw new InvalidOperationException("The type-aware gameobject Data0–23 catalog is incomplete or misaligned with current AzerothCore.");
var gameObjectData = new long[24]; gameObjectData[1] = 900200;
var gameObjectDraft = new GameObjectTemplateDraft(900200, 3, 12345, "Crucible's Cache", "", "Opening", "", 1.25f, gameObjectData, "", "",
    new(9000200, 0, 12, 34, 1, 1, -100f, 200f, 50f, 1.5f, 0, 0, 0.681639f, 0.731689f, 300, 255, 1, "", "Crucible spawn"),
    [new(900200, 6948, 0, 25, false, 1, 0, 1, 1, "Crucible chest loot")]);
var gameObjectPlan = GameObjectTemplateAdapter.CreatePlan(gameObjectDraft, GameObjectTemplateAdapter.CreatePortableCapabilities());
if (gameObjectPlan.Rows.Count != 3 || gameObjectPlan.Rows[0].Values["Data1"] is not 900200L || gameObjectPlan.Rows[1].Table != "gameobject" || gameObjectPlan.Rows[2].Table != "gameobject_loot_template" || !gameObjectPlan.PreviewSql().Contains("Crucible''s Cache"))
    throw new InvalidOperationException("Schema-aware gameobject template/spawn/loot planning failed.");
var questObjectPlan = GameObjectTemplateAdapter.CreatePlan(gameObjectDraft with { Type = 2, Spawn = null, Loot = [], StartsQuests = [123u], EndsQuests = [456u] }, GameObjectTemplateAdapter.CreatePortableCapabilities());
if (questObjectPlan.Rows.Count != 3 || questObjectPlan.Rows[1].Table != "gameobject_queststarter" || questObjectPlan.Rows[2].Table != "gameobject_questender")
    throw new InvalidOperationException("Gameobject quest starter/ender planning failed.");
try { _ = GameObjectTemplateAdapter.CreatePlan(gameObjectDraft with { Type = 5 }, GameObjectTemplateAdapter.CreatePortableCapabilities()); throw new InvalidOperationException("Gameobject loot was accepted for an incompatible type."); }
catch (InvalidDataException) { }

var behaviorDomains = BehaviorDomainCatalog.All;
var portableGossipOption = BehaviorAuthoringAdapter.PortableTable("gossip_menu_option");
var portableNpcText = BehaviorAuthoringAdapter.PortableTable("npc_text");
var portableCondition = BehaviorAuthoringAdapter.PortableTable("conditions");
var portableSmart = BehaviorAuthoringAdapter.PortableTable("smart_scripts");
if (behaviorDomains.Count != 9 || portableGossipOption.Columns.Count != 14 || portableNpcText.Columns.Count != 90 || portableCondition.Columns.Count != 15 || portableSmart.Columns.Count != 31)
    throw new InvalidOperationException("Behavior portable schema coverage drifted from the current WotLK world tables.");
if (BehaviorSemanticCatalog.SmartActions.Single(choice => choice.Value == 242).Name != "Increment data" || BehaviorSemanticCatalog.SmartEvents.Single(choice => choice.Value == 110).Name != "In melee range" || BehaviorSemanticCatalog.SmartTargets.Single(choice => choice.Value == 206).Name != "Formation" || BehaviorSemanticCatalog.ConditionTypes.Single(choice => choice.Value == 48).Name != "Quest objective progress")
    throw new InvalidOperationException("Behavior enum decoding no longer covers the current AzerothCore constants.");
var smartValues = new Dictionary<string, object?>(BehaviorAuthoringAdapter.Defaults(portableSmart), StringComparer.OrdinalIgnoreCase) { ["entryorguid"] = 900100, ["source_type"] = 0, ["id"] = 0, ["link"] = 0, ["event_type"] = 4, ["action_type"] = 11, ["action_param1"] = 133, ["target_type"] = 2, ["comment"] = "Crucible's SmartAI test" };
var smartPlan = BehaviorAuthoringAdapter.CreatePlan(BehaviorDomainCatalog.Find("smartai"), portableSmart, smartValues);
if (smartPlan.Rows.Count != 1 || smartPlan.Rows[0].Key.Count != 4 || smartPlan.Rows[0].Values["event_chance"]?.ToString() != "100" || !smartPlan.PreviewSql().Contains("Crucible''s SmartAI test", StringComparison.Ordinal))
    throw new InvalidOperationException("Complete SmartAI planning, composite identity, defaults, or SQL escaping failed.");
try { var invalid = new Dictionary<string, object?>(smartValues, StringComparer.OrdinalIgnoreCase) { ["event_chance"] = 101 }; _ = BehaviorAuthoringAdapter.CreatePlan(BehaviorDomainCatalog.Find("smartai"), portableSmart, invalid); throw new InvalidOperationException("Invalid SmartAI chance was accepted."); }
catch (InvalidDataException) { }
try { var invalid = new Dictionary<string, object?>(BehaviorAuthoringAdapter.Defaults(portableGossipOption), StringComparer.OrdinalIgnoreCase) { ["OptionIcon"] = 21 }; _ = BehaviorAuthoringAdapter.CreatePlan(BehaviorDomainCatalog.Find("gossip-option"), portableGossipOption, invalid); throw new InvalidOperationException("Invalid WotLK gossip icon was accepted."); }
catch (InvalidDataException) { }

var portableQuestTable = QuestTemplateAdapter.CreatePortableTable(); var portableQuestValues = QuestTemplateAdapter.CreateDefaultValues(portableQuestTable).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
portableQuestValues["ID"] = 900300u; portableQuestValues["LogTitle"] = "Crucible's Trial"; portableQuestValues["RequiredNpcOrGo1"] = -900200; portableQuestValues["RequiredNpcOrGoCount1"] = 1; portableQuestValues["RewardItem1"] = 6948; portableQuestValues["RewardAmount1"] = 1; portableQuestValues["Flags"] = 0x00001008u;
static DatabaseTableCapability QuestLinkTable(string name) => new(name, [new("id", "int", "int unsigned", false, "0", "PRI", "", 1), new("quest", "int", "int unsigned", false, "0", "PRI", "", 2)]);
var questCapabilities = new DatabaseCapabilities("fixture", "world", new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { [portableQuestTable.Name] = portableQuestTable, ["creature_queststarter"] = QuestLinkTable("creature_queststarter"), ["creature_questender"] = QuestLinkTable("creature_questender"), ["gameobject_queststarter"] = QuestLinkTable("gameobject_queststarter"), ["gameobject_questender"] = QuestLinkTable("gameobject_questender") });
var questPlan = QuestTemplateAdapter.CreatePlan(portableQuestTable, portableQuestValues, questCapabilities, new([900100], [900101], [900200], [900201]));
if (portableQuestTable.Columns.Count != 105 || questPlan.Rows.Count != 5 || questPlan.Rows[0].Values["QuestType"]?.ToString() != "2" || questPlan.Rows[1].Table != "creature_queststarter" || questPlan.Rows[4].Table != "gameobject_questender" || !questPlan.PreviewSql().Contains("Crucible''s Trial") || QuestTemplateAdapter.Group("RewardChoiceItemID6") != "Rewards" || QuestTemplateAdapter.Group("RequiredItemId6") != "Objectives")
    throw new InvalidOperationException("Complete schema-aware quest planning, defaults, grouping, or giver-link mapping failed.");
if (QuestSemanticCatalog.Types.Single(type => type.Value == 2).Name.Contains("normal", StringComparison.OrdinalIgnoreCase) == false || QuestSemanticCatalog.Flags.Single(flag => flag.Value == 0x00001000).Name != "Daily")
    throw new InvalidOperationException("Quest type or flag decoding drifted from the WotLK world schema/core semantics.");
try { var invalid = new Dictionary<string, object?>(portableQuestValues, StringComparer.OrdinalIgnoreCase) { ["LogTitle"] = "" }; _ = QuestTemplateAdapter.CreatePlan(portableQuestTable, invalid, questCapabilities); throw new InvalidOperationException("A quest without a title was accepted."); }
catch (InvalidDataException) { }

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
var generatedExportPreview = DbcRowExportService.Preview(WdbcFile.Load(generatedKeyPath), generatedSchema, ["Data"], [900, 999]);
if (generatedExportPreview.MatchingRows != 2 || generatedExportPreview.Columns.SequenceEqual(["$recordKey", "$rowIndex", "Data"]) == false ||
    Convert.ToUInt32(generatedExportPreview.Rows[0]["$recordKey"]) != 900 || Convert.ToInt32(generatedExportPreview.Rows[0]["$rowIndex"]) != 900)
    throw new InvalidOperationException("Schema-aware DBC export did not retain virtual GT record identity independently from physical float data.");
var generatedCsvExport = Path.Combine(Path.GetTempPath(), $"crucible-gt-export-{Guid.NewGuid():N}.csv");
var generatedExportResult = DbcRowExportService.Export(WdbcFile.Load(generatedKeyPath), generatedSchema, generatedCsvExport, new(DbcRowExportFormat.Csv, ["Data"], [900, 999]));
var generatedCsvLines = File.ReadAllLines(generatedCsvExport);
if (generatedExportResult.ExportedRows != 2 || generatedCsvLines.Length != 3 || generatedCsvLines[0] != "$recordKey,$rowIndex,Data" || !generatedCsvLines[1].StartsWith("900,900,", StringComparison.Ordinal))
    throw new InvalidOperationException("Atomic CSV DBC export omitted metadata, selected keys, or invariant physical values.");
File.Delete(generatedCsvExport);
var generatedJsonExport = Path.Combine(Path.GetTempPath(), $"crucible-gt-export-{Guid.NewGuid():N}.json");
DbcRowExportService.Export(WdbcFile.Load(generatedKeyPath), generatedSchema, generatedJsonExport, new(DbcRowExportFormat.Json, ["Data"], [900, 999]));
using (var generatedJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(generatedJsonExport)))
    if (generatedJson.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array || generatedJson.RootElement.GetArrayLength() != 2)
        throw new InvalidOperationException("DBC JSON array export did not produce a complete valid array.");
File.Delete(generatedJsonExport);
var cancelledExport = Path.Combine(Path.GetTempPath(), $"crucible-gt-cancelled-{Guid.NewGuid():N}.csv"); using (var exportCancellation = new CancellationTokenSource())
{
    exportCancellation.Cancel();
    try { _ = DbcRowExportService.Export(WdbcFile.Load(generatedKeyPath), generatedSchema, cancelledExport, new(DbcRowExportFormat.Csv), cancellationToken: exportCancellation.Token); throw new InvalidOperationException("A cancelled DBC export was published."); }
    catch (OperationCanceledException) { }
}
if (File.Exists(cancelledExport) || Directory.EnumerateFiles(Path.GetDirectoryName(cancelledExport)!, Path.GetFileName(cancelledExport) + ".crucible-*.tmp").Any())
    throw new InvalidOperationException("Cancelled DBC export left a published or temporary partial file.");
var spellExportFile = WdbcFile.Load(Path.Combine(args[1], "Spell.dbc")); var spellExportSchema = schema.ResolveColumns("Spell", spellExportFile.FieldCount);
var spellJsonLinesExport = Path.Combine(Path.GetTempPath(), $"crucible-spell-export-{Guid.NewGuid():N}.jsonl");
DbcRowExportService.Export(spellExportFile, spellExportSchema, spellJsonLinesExport, new(DbcRowExportFormat.JsonLines, ["ID", "Name_Lang[enUS]", "Description_Lang[enUS]", "Effect[0]"], [133]));
using (var spellExportJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(spellJsonLinesExport)))
    if (spellExportJson.RootElement.GetProperty("$recordKey").GetUInt32() != 133 || spellExportJson.RootElement.GetProperty("Name_Lang[enUS]").GetString() != "Fireball" || spellExportJson.RootElement.GetProperty("Description_Lang[enUS]").GetString()?.Length == 0)
        throw new InvalidOperationException("JSONL DBC export did not decode schema string offsets and preserve selected spell fields.");
var rawSpellPreview = DbcRowExportService.Preview(spellExportFile, spellExportSchema, ["Name_Lang[enUS]"], [133], rawStringOffsets: true);
if (rawSpellPreview.Rows[0]["Name_Lang[enUS]"] is not uint)
    throw new InvalidOperationException("DBC export raw-string mode did not expose the physical string-table offset.");
File.Delete(spellJsonLinesExport);
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
    if (SpellSqlAuditService.AzerothCoreSpellEntryFormat.Length != 234 || SpellSqlAuditService.AzerothCoreSpellEntryFormat[13] != 'x' || SpellSqlAuditService.AzerothCoreSpellEntryFormat[136] != 's')
        throw new InvalidOperationException("The exact AzerothCore SpellEntryfmt mapping is incomplete or misaligned.");
    var spellSqlColumns = Enumerable.Range(0, 234).Select(index => new DatabaseColumnCapability($"Sql_{index}",
        SpellSqlAuditService.AzerothCoreSpellEntryFormat[index] == 's' ? "varchar" : SpellSqlAuditService.AzerothCoreSpellEntryFormat[index] == 'f' ? "float" : "int",
        "fixture", true, null, index == 0 ? "PRI" : string.Empty, string.Empty, index + 1)).ToArray();
    var spellSqlTable = new DatabaseTableCapability("spell_dbc", spellSqlColumns);
    var spellSqlValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < 234; index++)
    {
        var format = SpellSqlAuditService.AzerothCoreSpellEntryFormat[index];
        spellSqlValues[spellSqlColumns[index].Name] = format switch
        {
            's' => spell.GetDisplayValue(0, spellColumns[index]),
            'f' => BitConverter.UInt32BitsToSingle(spell.GetRaw(0, spellColumns[index])),
            _ => unchecked((int)spell.GetRaw(0, spellColumns[index]))
        };
    }
    if (SpellSqlAuditService.CompareOverride(spell, 0, spellColumns, spellSqlTable, spellSqlValues).Count != 0)
        throw new InvalidOperationException("An identical spell_dbc record was reported as different from Spell.dbc.");
    spellSqlValues[spellSqlColumns[13].Name] = 123456789;
    if (SpellSqlAuditService.CompareOverride(spell, 0, spellColumns, spellSqlTable, spellSqlValues).Count != 0)
        throw new InvalidOperationException("A SpellEntryfmt-ignored SQL field created a false effective difference.");
    spellSqlValues[spellSqlColumns[42].Name] = unchecked((int)(spell.GetRaw(0, spellColumns[42]) + 1));
    var spellOverrideDifferences = SpellSqlAuditService.CompareOverride(spell, 0, spellColumns, spellSqlTable, spellSqlValues);
    if (spellOverrideDifferences.Count != 1 || spellOverrideDifferences[0].FieldIndex != 42 || SpellSqlAuditService.FindDbcRow(spell, spellColumns, spell.GetRaw(0, spellColumns[0])) != 0)
        throw new InvalidOperationException("Effective spell_dbc differences or exact Spell.dbc ID lookup were incorrect.");
    var namedSpellRow = Enumerable.Range(0, spell.RowCount).First(row => !string.IsNullOrWhiteSpace(Convert.ToString(spell.GetDisplayValue(row, spellColumns[136]))));
    var namedSpellId = spell.GetRaw(namedSpellRow, spellColumns[0]); var namedSpell = Convert.ToString(spell.GetDisplayValue(namedSpellRow, spellColumns[136]))!;
    var exactSpellReference = ReferenceLookupService.SearchDbc(ReferenceDomain.Spell, spell, spellColumns, 0, 136, namedSpellId.ToString(), 25, 39, 3);
    var nameSpellReference = ReferenceLookupService.SearchDbc(ReferenceDomain.Spell, spell, spellColumns, 0, 136, namedSpell[..Math.Min(5, namedSpell.Length)], 25, 39, 3);
    var mergedSpellReference = ReferenceLookupService.Merge(ReferenceDomain.Spell, namedSpellId.ToString(), 25, exactSpellReference,
        new ReferenceLookupPage(ReferenceDomain.Spell, namedSpellId.ToString(), [new(namedSpellId, namedSpell, "spell_dbc", "SQL override")], false, ["spell_dbc"]));
    if (exactSpellReference.Entries.Count != 1 || exactSpellReference.Entries[0].Id != namedSpellId || !nameSpellReference.Entries.Any(entry => entry.Id == namedSpellId) ||
        mergedSpellReference.Entries.Count != 1 || !mergedSpellReference.Entries[0].Source.Contains("Spell.dbc", StringComparison.OrdinalIgnoreCase) || !mergedSpellReference.Entries[0].Source.Contains("spell_dbc", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Shared DBC/SQL reference searching did not resolve names, exact IDs, or merge duplicate source identities.");
    var numericReferenceLayouts = new (string Table, int NameColumn, int[] Details)[]
    {
        ("SpellCastTimes", -1, [1, 2, 3]),
        ("SpellDuration", -1, [1, 2, 3]),
        ("SpellRange", 6, [1, 2, 3, 4, 5]),
        ("SpellRuneCost", -1, [1, 2, 3, 4]),
        ("SpellVisual", -1, [1, 2, 3, 4, 5, 6, 7, 8]),
        ("SpellIcon", 1, []),
        ("SpellDifficulty", -1, [1, 2, 3, 4])
    };
    foreach (var layout in numericReferenceLayouts)
    {
        var path = files.First(candidate => Path.GetFileName(candidate).Equals(layout.Table + ".dbc", StringComparison.OrdinalIgnoreCase));
        var dbc = WdbcFile.Load(path); var resolution = schema.ResolveColumns(layout.Table, dbc.FieldCount);
        if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch) throw new InvalidOperationException($"Reference picker test could not resolve {layout.Table}.dbc.");
        var referenceId = dbc.GetRaw(0, resolution.Columns[0]);
        var lookup = ReferenceLookupService.SearchDbc(ReferenceDomain.Spell, dbc, resolution.Columns, 0, layout.NameColumn, referenceId.ToString(), 5, layout.Details);
        if (!lookup.Entries.Any(entry => entry.Id == referenceId)) throw new InvalidOperationException($"Shared reference searching did not resolve numeric {layout.Table}.dbc ID {referenceId}.");
    }
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
    var mergeA = Path.Combine(builtDirectory, "merge-a.mpq"); var mergeB = Path.Combine(builtDirectory, "merge-b.mpq"); var mergeConflict = Path.Combine(builtDirectory, "merge-conflict.mpq");
    patchService.Create(mergeA, PatchInputMapper.Map([Path.Combine(overrideLayer, "SpellCastTimes.dbc")]));
    patchService.Create(mergeB, PatchInputMapper.Map([Path.Combine(overrideLayer, "SpellCastTimes.dbc"), animationPath]));
    var mergedPatch = Path.Combine(builtDirectory, "merge-output.mpq"); var merged = new MpqMergeService().Merge([mergeA, mergeB], mergedPatch);
    if (merged.OutputFiles != 2 || merged.ExactDuplicates != 1 || merged.Conflicts.Count != 0 || !patchService.Contains(mergedPatch, "DBFilesClient\\SpellCastTimes.dbc") || !patchService.Contains(mergedPatch, "DBFilesClient\\AnimationData.dbc"))
        throw new InvalidOperationException("MPQ merge failed to preserve unique paths and byte-deduplicate identical paths.");
    patchService.Create(mergeConflict, PatchInputMapper.Map([castTimesSource]));
    var blockedMergePath = Path.Combine(builtDirectory, "merge-blocked.mpq"); var blockedMerge = new MpqMergeService().Merge([mergeA, mergeConflict], blockedMergePath);
    if (blockedMerge.OutputFiles != 0 || blockedMerge.Conflicts.Count != 1 || File.Exists(blockedMergePath))
        throw new InvalidOperationException("MPQ merge did not block a different-byte internal-path conflict before creating output.");
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
var newlyCoveredSensitiveTables = new[] { "rbac_account_permissions", "auctionbidders", "character_arena_stats", "character_fishingsteps", "item_refund_instance", "item_soulbound_trade_data", "creature_respawn", "gameobject_respawn", "recovery_item" };
if (newlyCoveredSensitiveTables.Any(table => !LegacyDatabaseSnapshotService.IsSensitiveStateTable(table)) ||
    new[] { "mail_loot_template", "instance_template", "guild_rewards", "pet_levelstats", "linked_respawn" }.Any(LegacyDatabaseSnapshotService.IsSensitiveStateTable))
    throw new InvalidOperationException("Legacy SQL snapshot sensitive-state coverage regressed or swallowed reusable world definitions.");
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
var incompleteBaselineIdentity = snapshotManifest with { Source = snapshotManifest.Source with { CoreIdentity = new Dictionary<string, string> { ["core_version"] = "fixture", ["revision"] = "one" } } };
var incompleteLegacyIdentity = snapshotManifest with { Source = snapshotManifest.Source with { CoreIdentity = new Dictionary<string, string> { ["CORE_VERSION"] = "fixture" } } };
var differentLegacyIdentity = snapshotManifest with { Source = snapshotManifest.Source with { CoreIdentity = new Dictionary<string, string> { ["core_version"] = "different", ["revision"] = "one" } } };
if (LegacyDatabaseAuditService.DetermineBaselineIdentity(incompleteBaselineIdentity, incompleteLegacyIdentity) != LegacyDatabaseBaselineIdentity.Unknown ||
    LegacyDatabaseAuditService.DetermineBaselineIdentity(incompleteBaselineIdentity, differentLegacyIdentity) != LegacyDatabaseBaselineIdentity.DifferentCoreIdentity ||
    LegacyDatabaseAuditService.DetermineBaselineIdentity(incompleteBaselineIdentity, incompleteBaselineIdentity) != LegacyDatabaseBaselineIdentity.MatchingCoreIdentity)
    throw new InvalidOperationException("Legacy recovery overclaimed or missed baseline core-identity confidence.");
if (LegacyDatabaseDomainCatalog.Classify("player_class_stats") != LegacyDatabaseContentDomain.ClassesAndRaces ||
    LegacyDatabaseDomainCatalog.Classify("player_race_stats") != LegacyDatabaseContentDomain.ClassesAndRaces)
    throw new InvalidOperationException("Legacy recovery failed to classify current AzerothCore class/race stat tables.");
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

var recoveryRoot = Path.Combine(layerRoot, "legacy-recovery"); Directory.CreateDirectory(recoveryRoot);
var baselineRecoverySnapshot = Path.Combine(recoveryRoot, "stock.crucible-db-snapshot");
var legacyRecoverySnapshot = Path.Combine(recoveryRoot, "legacy.crucible-db-snapshot");
var noKeySchema = snapshotSchema with { Name = "custom_no_key", PrimaryKey = [] };
var textKeySchema = snapshotSchema with
{
    Name = "custom_text_key",
    PrimaryKey = ["name"],
    Columns = [snapshotColumns[1] with { Ordinal = 1, Key = "PRI" }]
};
var bitKeySchema = snapshotSchema with
{
    Name = "custom_bit_key",
    PrimaryKey = ["mask"],
    Columns = [snapshotColumns[0] with { Name = "mask", DataType = "bit", ColumnType = "bit(8)" }]
};
var unchangedTextKeySchema = textKeySchema with { Name = "custom_text_key_unchanged" };
WriteRecoverySnapshotFixture(baselineRecoverySnapshot, "stock_world",
    (snapshotSchema, "[[\"1\",\"Sword\"],[\"2\",\"Shield\"]]"),
    (noKeySchema, "[[\"1\",\"baseline\"]]"),
    (textKeySchema, "[[\"a\"],[\"B\"]]"),
    (unchangedTextKeySchema, "[[\"a\"],[\"B\"]]"),
    (bitKeySchema, "[[\"2\"],[\"10\"]]"));
WriteRecoverySnapshotFixture(legacyRecoverySnapshot, "legacy_world",
    (snapshotSchema, "[[\"1\",\"Crucible Sword\"],[\"3\",\"Axe\"]]"),
    (noKeySchema, "[[\"1\",\"custom\"]]"),
    (textKeySchema, "[[\"a\"],[\"C\"]]"),
    (unchangedTextKeySchema, "[[\"a\"],[\"B\"]]"),
    (bitKeySchema, "[[\"2\"],[\"11\"]]"));
var recoveryService = new LegacyDatabaseAuditService();
var recoveryArtifact = Path.Combine(recoveryRoot, "changes.crucible-db-audit");
var recoveryResult = recoveryService.AuditAsync(legacyRecoverySnapshot, recoveryArtifact, baselineRecoverySnapshot).GetAwaiter().GetResult();
var recoveryItem = recoveryResult.Manifest.Tables.Single(table => table.Name == "item_template");
var recoveryNoKey = recoveryResult.Manifest.Tables.Single(table => table.Name == "custom_no_key");
var recoveryTextKey = recoveryResult.Manifest.Tables.Single(table => table.Name == "custom_text_key");
var recoveryBitKey = recoveryResult.Manifest.Tables.Single(table => table.Name == "custom_bit_key");
var recoveryUnchangedTextKey = recoveryResult.Manifest.Tables.Single(table => table.Name == "custom_text_key_unchanged");
var recoveryChanges = ReadRecoveryChanges(recoveryService, recoveryArtifact, "item_template");
if (recoveryResult.Manifest.Mode != LegacyDatabaseAuditMode.BaselineCompared || recoveryResult.Manifest.BaselineIdentity != LegacyDatabaseBaselineIdentity.MatchingCoreIdentity ||
    recoveryResult.Manifest.PromotionReady || recoveryItem.AddedRows != 1 || recoveryItem.ModifiedRows != 1 || recoveryItem.RemovedRows != 1 || recoveryItem.ChangedFields != 5 ||
    recoveryNoKey.Status != LegacyDatabaseTableAuditStatus.BlockedNoPrimaryKey || recoveryNoKey.ChangeRecords != 0 ||
    recoveryTextKey.Status != LegacyDatabaseTableAuditStatus.BlockedIncompatibleSchema || recoveryTextKey.ChangeRecords != 0 ||
    recoveryUnchangedTextKey.Status != LegacyDatabaseTableAuditStatus.Unchanged || recoveryUnchangedTextKey.ChangeRecords != 0 ||
    recoveryBitKey.Status != LegacyDatabaseTableAuditStatus.Changed || recoveryBitKey.AddedRows != 1 || recoveryBitKey.RemovedRows != 1 ||
    recoveryChanges.Count != 3 || recoveryChanges.Single(change => change.Kind == LegacyDatabaseRowChangeKind.Modified).Fields.Single().Column != "name" ||
    recoveryChanges.Any(change => change.PromotionApproved))
    throw new InvalidOperationException("Baseline-to-legacy SQL recovery audit did not preserve safe field-level additions, edits, removals, or no-PK blocking.");
var recoveryInspection = recoveryService.InspectAsync(recoveryArtifact).GetAwaiter().GetResult();
if (!recoveryInspection.Valid || recoveryInspection.Manifest?.Legacy.ArtifactSha256.Length != 64)
    throw new InvalidOperationException($"Legacy SQL recovery audit validation failed: {string.Join("; ", recoveryInspection.Findings)}");

AssertRehashedRecoveryTamperRejected(recoveryService, recoveryArtifact, Path.Combine(recoveryRoot, "tampered-domain.crucible-db-audit"), "domain",
    manifest => manifest with
    {
        Tables = manifest.Tables.Select(table => table.Name == "item_template" ? table with { Domain = LegacyDatabaseContentDomain.Pets } : table).ToArray()
    });
AssertRehashedRecoveryTamperRejected(recoveryService, recoveryArtifact, Path.Combine(recoveryRoot, "tampered-status.crucible-db-audit"), "status",
    manifest => manifest with
    {
        Tables = manifest.Tables.Select(table => table.Name == "item_template" ? table with { Status = LegacyDatabaseTableAuditStatus.Unchanged } : table).ToArray()
    });
AssertRehashedRecoveryTamperRejected(recoveryService, recoveryArtifact, Path.Combine(recoveryRoot, "tampered-key.crucible-db-audit"), "key", changeMutation: (table, changes) =>
    table == "item_template"
        ? changes.Select((change, index) => index == 0
            ? change with { Key = change.Key.Select((part, partIndex) => partIndex == 0 ? part with { Value = LegacyDatabaseAuditValue.Missing } : part).ToArray() }
            : change).ToArray()
        : changes);
AssertRehashedRecoveryTamperRejected(recoveryService, recoveryArtifact, Path.Combine(recoveryRoot, "tampered-value.crucible-db-audit"), "semantics", changeMutation: (table, changes) =>
    table == "item_template"
        ? changes.Select(change => change.Kind == LegacyDatabaseRowChangeKind.Added
            ? change with
            {
                Fields = change.Fields.Select((field, index) => index == 0
                    ? field with { Baseline = new(LegacyDatabaseAuditValueState.Scalar, "forged baseline") }
                    : field).ToArray()
            }
            : change).ToArray()
        : changes);

var emptyBaselineSnapshot = Path.Combine(recoveryRoot, "empty-stock.crucible-db-snapshot");
var emptyLegacySnapshot = Path.Combine(recoveryRoot, "empty-legacy.crucible-db-snapshot");
var emptyBaselineOnlySchema = snapshotSchema with { Name = "empty_baseline_only" };
var emptyLegacyOnlySchema = snapshotSchema with { Name = "empty_legacy_only" };
WriteRecoverySnapshotFixture(emptyBaselineSnapshot, "stock_world", (emptyBaselineOnlySchema, "[]"));
WriteRecoverySnapshotFixture(emptyLegacySnapshot, "legacy_world", (emptyLegacyOnlySchema, "[]"));
var emptyComparedArtifact = Path.Combine(recoveryRoot, "empty-one-sided.crucible-db-audit");
var emptyCompared = recoveryService.AuditAsync(emptyLegacySnapshot, emptyComparedArtifact, emptyBaselineSnapshot).GetAwaiter().GetResult();
var emptyComparedInspection = recoveryService.InspectAsync(emptyComparedArtifact).GetAwaiter().GetResult();
if (!emptyComparedInspection.Valid || emptyCompared.Manifest.TotalChangeRecords != 0 || emptyCompared.Manifest.Tables.Any(table => table.ChangeRecords != 0))
    throw new InvalidOperationException($"One-sided empty SQL tables did not produce a valid zero-change audit: {string.Join("; ", emptyComparedInspection.Findings)}");

var emptyUnattributedArtifact = Path.Combine(recoveryRoot, "empty-unattributed.crucible-db-audit");
var emptyUnattributed = recoveryService.AuditAsync(emptyLegacySnapshot, emptyUnattributedArtifact).GetAwaiter().GetResult();
var emptyUnattributedInspection = recoveryService.InspectAsync(emptyUnattributedArtifact).GetAwaiter().GetResult();
if (!emptyUnattributedInspection.Valid || emptyUnattributed.Manifest.TotalChangeRecords != 0 || emptyUnattributed.Manifest.Tables.Single().ChangeRecords != 0)
    throw new InvalidOperationException($"Baseline-free empty SQL tables did not produce a valid zero-change audit: {string.Join("; ", emptyUnattributedInspection.Findings)}");

var invalidRecoverySnapshot = Path.Combine(recoveryRoot, "invalid-cell.crucible-db-snapshot");
WriteRecoverySnapshotFixture(invalidRecoverySnapshot, "legacy_world", (snapshotSchema, "[[\"1\",true]]"));
var preservedRecoveryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(recoveryArtifact)));
try
{
    _ = recoveryService.AuditAsync(invalidRecoverySnapshot, recoveryArtifact, baselineRecoverySnapshot, new(Overwrite: true)).GetAwaiter().GetResult();
    throw new InvalidOperationException("Recovery audit accepted a noncanonical snapshot value.");
}
catch (InvalidDataException exception) when (exception.Message.Contains("neither null", StringComparison.OrdinalIgnoreCase)) { }
if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(recoveryArtifact))) != preservedRecoveryHash ||
    !recoveryService.InspectAsync(recoveryArtifact).GetAwaiter().GetResult().Valid)
    throw new InvalidOperationException("A failed overwrite audit destroyed or changed the previous valid artifact.");

var binaryNumericSnapshot = Path.Combine(recoveryRoot, "binary-numeric.crucible-db-snapshot");
WriteRecoverySnapshotFixture(binaryNumericSnapshot, "legacy_world", (snapshotSchema, "[[{\"$binary\":\"AQ==\"},\"Binary key\"]]"));
AssertRecoverySnapshotRejected(recoveryService, binaryNumericSnapshot, Path.Combine(recoveryRoot, "binary-numeric.crucible-db-audit"), "numeric column encoded as $binary");
var nullNonNullableSnapshot = Path.Combine(recoveryRoot, "null-nonnullable.crucible-db-snapshot");
WriteRecoverySnapshotFixture(nullNonNullableSnapshot, "legacy_world", (snapshotSchema, "[[\"1\",null]]"));
AssertRecoverySnapshotRejected(recoveryService, nullNonNullableSnapshot, Path.Combine(recoveryRoot, "null-nonnullable.crucible-db-audit"), "null in a non-nullable column");

var unattributedArtifact = Path.Combine(recoveryRoot, "unattributed.crucible-db-audit");
var unattributed = recoveryService.AuditAsync(legacyRecoverySnapshot, unattributedArtifact).GetAwaiter().GetResult();
var unattributedItem = unattributed.Manifest.Tables.Single(table => table.Name == "item_template");
if (unattributed.Manifest.Mode != LegacyDatabaseAuditMode.Unattributed || unattributed.Manifest.Baseline is not null || unattributedItem.UnattributedRows != 2 ||
    unattributedItem.AddedRows != 0 || ReadRecoveryChanges(recoveryService, unattributedArtifact, "item_template").Any(change => change.Attribution != LegacyDatabaseAuditAttribution.Unattributed))
    throw new InvalidOperationException("Baseline-free SQL recovery incorrectly labeled stock rows as proven custom additions or edits.");

var protectedLegacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyRecoverySnapshot)));
try
{
    _ = recoveryService.AuditAsync(legacyRecoverySnapshot, legacyRecoverySnapshot, baselineRecoverySnapshot, new(Overwrite: true)).GetAwaiter().GetResult();
    throw new InvalidOperationException("Legacy recovery allowed an audit output to alias and overwrite its input snapshot.");
}
catch (ArgumentException) { }
if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyRecoverySnapshot))) != protectedLegacyHash)
    throw new InvalidOperationException("Legacy recovery modified an input snapshot while rejecting an aliased output path.");

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

static string WriteGraphAsset(string contentRoot,string provenance,string clientPath,byte[] bytes)
{
    var normalized=PatchInputMapper.NormalizeArchivePath(clientPath);var output=Path.Combine(contentRoot,Path.GetDirectoryName(normalized)!,provenance,Path.GetFileName(normalized));Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllBytes(output,bytes);return output;
}

static byte[] GraphStrings(params string[] values)=>System.Text.Encoding.UTF8.GetBytes(string.Join('\0',values)+'\0');

static byte[] GraphChunks(params (string Id,byte[] Data)[] chunks)
{
    using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true);foreach(var chunk in chunks){if(chunk.Id.Length!=4)throw new ArgumentException("Chunk IDs require four characters.");writer.Write(System.Text.Encoding.ASCII.GetBytes(new string(chunk.Id.Reverse().ToArray())));writer.Write((uint)chunk.Data.Length);writer.Write(chunk.Data);}writer.Flush();return stream.ToArray();
}

static void WriteRecoverySnapshotFixture(string path, string database, params (LegacyDatabaseTableSchema Schema, string RowsJson)[] sources)
{
    var tables = new List<LegacyDatabaseSnapshotTable>();
    using (var stream = File.Create(path))
    using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
    {
        foreach (var source in sources)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(source.RowsJson);
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            var rows = document.RootElement.GetArrayLength();
            var entryName = $"tables/{Uri.EscapeDataString(source.Schema.Name)}.rows.json";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            tables.Add(new(source.Schema.Name, source.Schema.TableType, source.Schema.Engine, source.Schema.Collation, source.Schema.Comment,
                rows, source.Schema.PrimaryKey, source.Schema.Columns, LegacyDatabaseSnapshotService.ComputeSchemaHash(source.Schema), entryName,
                source.Schema.PrimaryKey.Count == 0 ? "capture-order (table has no primary key)" : string.Join(',', source.Schema.PrimaryKey), rows, bytes.Length, hash));
            using var rowsStream = archive.CreateEntry(entryName).Open(); rowsStream.Write(bytes);
        }
        var ordered = tables.OrderBy(table => table.Name, StringComparer.Ordinal).ToArray();
        var manifest = new LegacyDatabaseSnapshotManifest(LegacyDatabaseSnapshotService.ArtifactFormat, LegacyDatabaseSnapshotService.ArtifactFormatVersion,
            "fixture", DateTimeOffset.UtcNow, new(database, "fixture server", "fixture database", "utf8", "utf8_general_ci",
                new Dictionary<string, string> { ["core_version"] = "same-stock-revision" }), new([], [], false, ["account"]), ordered,
            ordered.Sum(table => table.Rows), LegacyDatabaseSnapshotService.ComputeSchemaAggregateHash(ordered.Select(table => (table.Name, table.SchemaSha256))),
            LegacyDatabaseSnapshotService.ComputeAggregateHash(ordered.Select(table => (table.Name, table.RowsSha256, table.Rows))), true, true);
        using var manifestStream = archive.CreateEntry("manifest.json").Open();
        System.Text.Json.JsonSerializer.Serialize(manifestStream, manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}

static List<LegacyDatabaseRowChange> ReadRecoveryChanges(LegacyDatabaseAuditService service, string artifact, string table)
{
    var result = new List<LegacyDatabaseRowChange>();
    var enumerator = service.ReadChangesAsync(artifact, table).GetAsyncEnumerator();
    try { while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) result.Add(enumerator.Current); }
    finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    return result;
}

static void AssertRecoverySnapshotRejected(LegacyDatabaseAuditService service, string snapshot, string output, string scenario)
{
    try
    {
        _ = service.AuditAsync(snapshot, output).GetAwaiter().GetResult();
    }
    catch (InvalidDataException)
    {
        if (File.Exists(output)) throw new InvalidOperationException($"Recovery published an artifact after rejecting {scenario}.");
        return;
    }
    if (File.Exists(output)) File.Delete(output);
    throw new InvalidOperationException($"Recovery accepted a snapshot containing {scenario}.");
}

static void AssertRehashedRecoveryTamperRejected(
    LegacyDatabaseAuditService service,
    string source,
    string destination,
    string expectedFinding,
    Func<LegacyDatabaseAuditManifest, LegacyDatabaseAuditManifest>? manifestMutation = null,
    Func<string, IReadOnlyList<LegacyDatabaseRowChange>, IReadOnlyList<LegacyDatabaseRowChange>>? changeMutation = null)
{
    RewriteRecoveryArtifact(source, destination, manifestMutation, changeMutation);
    var inspection = service.InspectAsync(destination, verifyChanges: true).GetAwaiter().GetResult();
    if (inspection.Valid || !inspection.Findings.Any(finding => finding.Contains(expectedFinding, StringComparison.OrdinalIgnoreCase)) ||
        inspection.Findings.Any(finding => finding.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException($"A fully rehashed recovery artifact with invalid {expectedFinding} semantics was not rejected cleanly: {string.Join("; ", inspection.Findings)}");
    try
    {
        _ = ReadRecoveryChanges(service, destination, "item_template");
        throw new InvalidOperationException($"ReadChangesAsync consumed a fully rehashed recovery artifact with invalid {expectedFinding} semantics.");
    }
    catch (InvalidDataException) { }
}

static void RewriteRecoveryArtifact(
    string source,
    string destination,
    Func<LegacyDatabaseAuditManifest, LegacyDatabaseAuditManifest>? manifestMutation,
    Func<string, IReadOnlyList<LegacyDatabaseRowChange>, IReadOnlyList<LegacyDatabaseRowChange>>? changeMutation)
{
    var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General) { WriteIndented = true };
    json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    LegacyDatabaseAuditManifest manifest;
    var changeEntries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    using (var input = File.OpenRead(source))
    using (var archive = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read))
    {
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        manifest = System.Text.Json.JsonSerializer.Deserialize<LegacyDatabaseAuditManifest>(manifestStream, json)
                   ?? throw new InvalidDataException("Recovery fixture manifest is empty.");
        foreach (var table in manifest.Tables.Where(table => table.DataEntry is not null))
        {
            using var entryStream = archive.GetEntry(table.DataEntry!)!.Open();
            using var memory = new MemoryStream(); entryStream.CopyTo(memory);
            changeEntries[table.DataEntry!] = memory.ToArray();
        }
    }

    var rewrittenTables = new List<LegacyDatabaseTableAudit>(manifest.Tables.Count);
    foreach (var table in manifest.Tables)
    {
        if (table.DataEntry is null) { rewrittenTables.Add(table); continue; }
        var changes = System.Text.Json.JsonSerializer.Deserialize<List<LegacyDatabaseRowChange>>(changeEntries[table.DataEntry], json)
                      ?? throw new InvalidDataException($"Recovery fixture changes are empty for {table.Name}.");
        var rewritten = changeMutation?.Invoke(table.Name, changes) ?? changes;
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rewritten, json);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        changeEntries[table.DataEntry] = bytes;
        rewrittenTables.Add(table with { UncompressedBytes = bytes.Length, ChangesSha256 = hash });
    }
    manifest = manifest with { Tables = rewrittenTables };
    manifest = manifest with
    {
        ChangesSha256 = LegacyDatabaseSnapshotService.ComputeAggregateHash(manifest.Tables.Where(table => table.DataEntry is not null)
            .OrderBy(table => table.Name, StringComparer.Ordinal).Select(table => (table.Name, table.ChangesSha256!, table.ChangeRecords)))
    };
    manifest = manifestMutation?.Invoke(manifest) ?? manifest;

    using var output = File.Create(destination);
    using var destinationArchive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create);
    foreach (var entry in changeEntries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
    {
        using var entryStream = destinationArchive.CreateEntry(entry.Key).Open(); entryStream.Write(entry.Value);
    }
    using var rewrittenManifestStream = destinationArchive.CreateEntry("manifest.json").Open();
    System.Text.Json.JsonSerializer.Serialize(rewrittenManifestStream, manifest, json);
}

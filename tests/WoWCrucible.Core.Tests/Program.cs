using WoWCrucible.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 2)
    throw new ArgumentException("Usage: WoWCrucible.Core.Tests <schema.xml> <dbc-directory>");

if (CrucibleCommandCatalog.All.Count < 25 || CrucibleCommandCatalog.All.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count() != CrucibleCommandCatalog.All.Count ||
    CrucibleCommandCatalog.Search("heidi favorites").FirstOrDefault()?.Command.Id != "workspace.sql" ||
    CrucibleCommandCatalog.Search("foreign key constraint").FirstOrDefault()?.Command.Id != "workspace.sql" ||
    CrucibleCommandCatalog.Search("mpq merge").FirstOrDefault()?.Command.Id != "workspace.mpq" ||
    CrucibleCommandCatalog.Search("casc later client extract").FirstOrDefault()?.Command.Id != "workspace.mpq" ||
    CrucibleCommandCatalog.Search("cut unobtainable item").FirstOrDefault()?.Command.Id != "workspace.items" ||
    CrucibleCommandCatalog.Search("project collision ids").FirstOrDefault()?.Command.Id != "workspace.projects" ||
    CrucibleCommandCatalog.Search("pet companion level stats").FirstOrDefault()?.Command.Id != "workspace.pets" ||
    CrucibleCommandCatalog.Search("pet level curve scale").FirstOrDefault()?.Command.Id != "workspace.pets" ||
    CrucibleCommandCatalog.Search("pet family growth compare graph").FirstOrDefault()?.Command.Id != "workspace.pets" ||
    CrucibleCommandCatalog.Search("pet talent ability evidence graph").FirstOrDefault()?.Command.Id != "workspace.pets" ||
    CrucibleCommandCatalog.Search("model viewer animation").FirstOrDefault()?.Command.Id != "workspace.assets" ||
    CrucibleCommandCatalog.Search("wiki field help").FirstOrDefault()?.Command.Id != "workspace.knowledge" ||
    CrucibleCommandCatalog.Search("words-that-match-nothing").Count != 0)
    throw new InvalidOperationException("Shared desktop/CLI command catalog uniqueness, aliases, multi-term filtering, or ranking regressed.");

if (Environment.Is64BitProcess && CascArchiveService.NativeFindDataSize != 344)
    throw new InvalidOperationException($"CascLib x64 find-data ABI regressed: expected 344 bytes, found {CascArchiveService.NativeFindDataSize}.");
if (OperatingSystem.IsWindows() && Environment.Is64BitProcess && !CascArchiveService.IsNativeProviderAvailable())
    throw new InvalidOperationException("The pinned CascLib native provider could not be loaded from the test output.");

var designTable = new DatabaseTableCapability("fixture_table",
[
    new("id", "int", "int unsigned", false, null, "PRI", "auto_increment", 1),
    new("kind", "enum", "enum('a','b')", false, "a", "MUL", "", 2),
    new("note", "varchar", "varchar(64)", true, null, "", "", 3)
]);
var designCreate = """
CREATE TABLE `fixture_table` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `kind` enum('a','b') NOT NULL DEFAULT 'a',
  `note` varchar(64) DEFAULT NULL COMMENT 'hello,world',
  PRIMARY KEY (`id`),
  KEY `ix_kind` (`kind`),
  CONSTRAINT `fk_fixture_kind` FOREIGN KEY (`kind`) REFERENCES `kind_catalog` (`code`) ON DELETE RESTRICT ON UPDATE CASCADE,
  CONSTRAINT `chk_fixture_note` CHECK (((`note` is null) or (char_length(`note`) <= 64)))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
""";
var designColumns = SqlTableDesignerService.ParseColumns(designCreate, designTable);
if (designColumns.Count != 3 || designColumns[1].Definition != "enum('a','b') NOT NULL DEFAULT 'a'" || !designColumns[2].Definition.Contains("hello,world", StringComparison.Ordinal))
    throw new InvalidOperationException("Exact SHOW CREATE TABLE column parsing lost enum members, defaults, comments, or ordering.");
var designForeignKeys = SqlTableDesignerService.ParseForeignKeys(designCreate);
var designChecks = SqlTableDesignerService.ParseCheckConstraints(designCreate);
if (designForeignKeys.Count != 1 || designForeignKeys[0].Name != "fk_fixture_kind" || designForeignKeys[0].Columns.Count != 1 || designForeignKeys[0].ReferencedTable != "kind_catalog" || designForeignKeys[0].DeleteRule != "RESTRICT" || designForeignKeys[0].UpdateRule != "CASCADE" ||
    designChecks.Count != 1 || designChecks[0].Name != "chk_fixture_note" || !designChecks[0].Expression.Contains("char_length", StringComparison.OrdinalIgnoreCase) || !designChecks[0].Enforced)
    throw new InvalidOperationException("Exact SHOW CREATE TABLE constraint parsing lost a foreign-key column/rule or nested CHECK expression.");
var designReferenceTable = new DatabaseTableCapability("kind_catalog", [new("code", "enum", "enum('a','b')", false, null, "PRI", "", 1)]);
var designTables = new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { [designTable.Name] = designTable, [designReferenceTable.Name] = designReferenceTable };
var designSnapshot = new SqlTableDesignSnapshot("fixture_db", "fixture_table", designCreate, "fixture-hash", designColumns,
    [new("ix_kind", false, "BTREE", ["kind"], 2, "")], [], designTables, designForeignKeys, designChecks, "MySQL 8.4");
var designProfile = new DatabaseConnectionProfile("127.0.0.1", 3306, "tester", "", "fixture_db");
var designService = new SqlTableDesignerService();
var addColumnPlan = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.AddColumn, NewName: "flags", Definition: "int unsigned NOT NULL DEFAULT '0'", Placement: SqlColumnPlacement.After, AfterColumn: "kind"));
var renameColumnPlan = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.RenameColumn, ColumnName: "kind", NewName: "category", Definition: designColumns[1].Definition));
if (addColumnPlan.Sql != "ALTER TABLE `fixture_table` ADD COLUMN `flags` int unsigned NOT NULL DEFAULT '0' AFTER `kind`;" ||
    !renameColumnPlan.Sql.StartsWith("ALTER TABLE `fixture_table` CHANGE COLUMN `kind` `category`", StringComparison.Ordinal) ||
    !renameColumnPlan.Warnings.Any(warning => warning.Contains("ix_kind", StringComparison.Ordinal)) || !renameColumnPlan.Warnings.Any(warning => warning.Contains("fk_fixture_kind", StringComparison.Ordinal)))
    throw new InvalidOperationException("Stale-bound table designer did not produce exact ADD/RENAME DDL or dependency warnings.");
var addForeignKeyPlan = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.AddForeignKey, NewName: "fk_fixture_kind_2", Columns: ["kind"], ReferencedTable: "kind_catalog", ReferencedColumns: ["code"], DeleteRule: "NO ACTION", UpdateRule: "CASCADE"));
var dropForeignKeyPlan = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.DropForeignKey, ColumnName: "fk_fixture_kind"));
var addCheckPlan = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.AddCheckConstraint, NewName: "chk_fixture_id", CheckExpression: "`id` > 0 AND (`note` IS NULL OR char_length(`note`) <= 64)"));
var dropCheckPlan = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.DropCheckConstraint, ColumnName: "chk_fixture_note"));
var mariaDropCheckPlan = designService.Prepare(designProfile, designSnapshot with { ServerVersion = "10.11.8-MariaDB" }, new(SqlTableDesignOperation.DropCheckConstraint, ColumnName: "chk_fixture_note"));
if (addForeignKeyPlan.Sql != "ALTER TABLE `fixture_table` ADD CONSTRAINT `fk_fixture_kind_2` FOREIGN KEY (`kind`) REFERENCES `kind_catalog` (`code`) ON DELETE NO ACTION ON UPDATE CASCADE;" ||
    dropForeignKeyPlan.Sql != "ALTER TABLE `fixture_table` DROP FOREIGN KEY `fk_fixture_kind`;" ||
    addCheckPlan.Sql != "ALTER TABLE `fixture_table` ADD CONSTRAINT `chk_fixture_id` CHECK (`id` > 0 AND (`note` IS NULL OR char_length(`note`) <= 64));" ||
    dropCheckPlan.Sql != "ALTER TABLE `fixture_table` DROP CHECK `chk_fixture_note`;" || mariaDropCheckPlan.Sql != "ALTER TABLE `fixture_table` DROP CONSTRAINT `chk_fixture_note`;")
    throw new InvalidOperationException("Reviewed foreign-key/CHECK plans lost exact identifiers, column order, actions, or MySQL/MariaDB drop syntax.");
try { _ = SqlTableDesignerService.ValidateDefinition("int NOT NULL; DROP TABLE item_template"); throw new InvalidOperationException("Guided column definition accepted a second statement."); }
catch (ArgumentException) { }
try { _ = SqlTableDesignerService.ValidateDefinition("int NOT NULL, DROP COLUMN id"); throw new InvalidOperationException("Guided column definition accepted a second ALTER clause."); }
catch (ArgumentException) { }
try { _ = SqlTableDesignerService.ValidateCheckExpression("`id` > 0); DROP TABLE item_template;"); throw new InvalidOperationException("Guided CHECK expression accepted a second statement."); }
catch (ArgumentException) { }
try { _ = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.AddForeignKey, NewName: "fk_bad", Columns: ["note"], ReferencedTable: "kind_catalog", ReferencedColumns: ["missing"])); throw new InvalidOperationException("Foreign-key planning accepted an unknown referenced column."); }
catch (ArgumentException) { }
try { _ = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.AddForeignKey, NewName: "fk_null_bad", Columns: ["kind"], ReferencedTable: "kind_catalog", ReferencedColumns: ["code"], DeleteRule: "SET NULL")); throw new InvalidOperationException("Foreign-key planning accepted SET NULL for a non-nullable source column."); }
catch (ArgumentException exception) when (exception.Message.Contains("nullable", StringComparison.OrdinalIgnoreCase)) { }
try { _ = designService.Prepare(designProfile, designSnapshot, new(SqlTableDesignOperation.AddCheckConstraint, NewName: "chk_fixture_note", CheckExpression: "`id` > 0")); throw new InvalidOperationException("Constraint planning accepted a duplicate identity."); }
catch (ArgumentException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)) { }

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

var wdb2FixtureRoot = Path.Combine(Path.GetTempPath(), $"crucible-wdb2-{Guid.NewGuid():N}");
Directory.CreateDirectory(wdb2FixtureRoot);
try
{
    var itemDbd = Path.Combine(wdb2FixtureRoot, "Item.dbd");
    File.WriteAllText(itemDbd, """
COLUMNS
int ID
int ClassID
int SubclassID
int Sound_override_subclassID
int Material
int DisplayInfoID
int InventoryType
int SheatheType

BUILD 4.0.0.11792-4.3.4.15595
$id$ID<32>
ClassID<32>
SubclassID<32>
Sound_override_subclassID<32>
Material<32>
DisplayInfoID<32>
InventoryType<32>
SheatheType<32>
""");
    var itemDb2 = Path.Combine(wdb2FixtureRoot, "Item.db2");
    var itemBytes = new byte[48 + 2 * 32 + 1];
    "WDB2"u8.CopyTo(itemBytes);
    BinaryPrimitives.WriteInt32LittleEndian(itemBytes.AsSpan(4, 4), 2);
    BinaryPrimitives.WriteInt32LittleEndian(itemBytes.AsSpan(8, 4), 8);
    BinaryPrimitives.WriteInt32LittleEndian(itemBytes.AsSpan(12, 4), 32);
    BinaryPrimitives.WriteInt32LittleEndian(itemBytes.AsSpan(16, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(itemBytes.AsSpan(20, 4), 0x50238EC2);
    BinaryPrimitives.WriteInt32LittleEndian(itemBytes.AsSpan(24, 4), 15595);
    BinaryPrimitives.WriteUInt32LittleEndian(itemBytes.AsSpan(40, 4), uint.MaxValue);
    var fixtureValues = new uint[]
    {
        17, 2, 7, uint.MaxValue, 1, 100, 0, 3,
        17802, 4, 1, 0, 2, 200, 13, 1
    };
    for (var index = 0; index < fixtureValues.Length; index++)
        BinaryPrimitives.WriteUInt32LittleEndian(itemBytes.AsSpan(48 + index * 4, 4), fixtureValues[index]);
    File.WriteAllBytes(itemDb2, itemBytes);

    var itemTable = WdbcFile.Load(itemDb2);
    var itemSchema = DbdSchemaService.ResolveFile(itemDbd, 15595, itemTable.FieldCount, itemTable.RecordSize);
    if (itemTable.ContainerKind != ClientTableContainerKind.Wdb2 || itemTable.LogicalTableName != "Item" || itemTable.RowCount != 2 ||
        itemTable.Db2Metadata is not { Build: 15595, TableHash: 0x50238EC2, Locale: uint.MaxValue } || itemSchema.Columns.Count != 8 ||
        !Equals(itemTable.GetDisplayValue(0, itemSchema.Columns.Single(column => column.Name == "Sound_override_subclassID")), -1))
        throw new InvalidOperationException("Fixed-layout WDB2 header, DBD layout, or signed scalar decoding regressed.");

    var roundTripDb2 = Path.Combine(wdb2FixtureRoot, "Item-roundtrip.db2");
    itemTable.SaveAs(roundTripDb2, false);
    if (!File.ReadAllBytes(roundTripDb2).SequenceEqual(itemBytes) || !File.Exists(roundTripDb2 + ".crucible-table.json") ||
        !File.ReadAllText(roundTripDb2 + ".crucible-table.json").Contains("\"Container\": \"Wdb2\"", StringComparison.Ordinal))
        throw new InvalidOperationException("An unchanged renamed WDB2 did not round-trip byte-for-byte with its logical table identity.");
    var renamedTable = WdbcFile.Load(roundTripDb2);
    if (renamedTable.LogicalTableName != "Item" || renamedTable.Db2Metadata?.Build != 15595)
        throw new InvalidOperationException("A renamed WDB2 did not recover its definition identity from the sidecar.");

    var inventoryType = itemSchema.Columns.Single(column => column.Name == "InventoryType");
    renamedTable.SetDisplayValue(0, inventoryType, 5);
    var editedDb2 = Path.Combine(wdb2FixtureRoot, "Item-edited.db2");
    renamedTable.SaveAs(editedDb2, false);
    var editedTable = WdbcFile.Load(editedDb2);
    if (editedTable.GetRaw(0, inventoryType) != 5 || editedTable.Db2Metadata is not { Build: 15595, TableHash: 0x50238EC2 })
        throw new InvalidOperationException("A fixed-layout WDB2 cell edit changed metadata or did not persist.");
    var itemIdColumn = itemSchema.Columns.Single(column => column.IsIndex);
    var newRow = editedTable.AddBlankRow(itemIdColumn);
    if (newRow != 2 || editedTable.RowCount != 3 || editedTable.GetRaw(newRow, itemIdColumn) != 17803)
        throw new InvalidOperationException("A simple WDB2 could not safely allocate a new physical row and ID.");

    var complexDb2 = Path.Combine(wdb2FixtureRoot, "Complex.db2");
    var complexBytes = new byte[48 + 6 + 4 + 1 + 8];
    "WDB2"u8.CopyTo(complexBytes);
    BinaryPrimitives.WriteInt32LittleEndian(complexBytes.AsSpan(4, 4), 1);
    BinaryPrimitives.WriteInt32LittleEndian(complexBytes.AsSpan(8, 4), 1);
    BinaryPrimitives.WriteInt32LittleEndian(complexBytes.AsSpan(12, 4), 4);
    BinaryPrimitives.WriteInt32LittleEndian(complexBytes.AsSpan(16, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(complexBytes.AsSpan(20, 4), 0xAABBCCDD);
    BinaryPrimitives.WriteInt32LittleEndian(complexBytes.AsSpan(24, 4), 15595);
    BinaryPrimitives.WriteUInt32LittleEndian(complexBytes.AsSpan(32, 4), 10);
    BinaryPrimitives.WriteUInt32LittleEndian(complexBytes.AsSpan(36, 4), 10);
    BinaryPrimitives.WriteUInt32LittleEndian(complexBytes.AsSpan(40, 4), uint.MaxValue);
    BinaryPrimitives.WriteInt32LittleEndian(complexBytes.AsSpan(44, 4), 8);
    BinaryPrimitives.WriteInt32LittleEndian(complexBytes.AsSpan(48, 4), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(complexBytes.AsSpan(52, 2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(complexBytes.AsSpan(54, 4), 10);
    BinaryPrimitives.WriteUInt32LittleEndian(complexBytes.AsSpan(59, 4), 20);
    BinaryPrimitives.WriteUInt32LittleEndian(complexBytes.AsSpan(63, 4), 10);
    File.WriteAllBytes(complexDb2, complexBytes);
    var complexTable = WdbcFile.Load(complexDb2);
    var complexId = new DbcColumn(0, 0, 4, "ID", DbcValueType.UInt32, true);
    if (complexTable.AllowsStructuralMutation || complexTable.Db2Metadata is not { CopyRows: 1 } || !complexTable.Db2Metadata.HasIndexMap)
        throw new InvalidOperationException("WDB2 index/copy side tables were not detected.");
    var complexRoundTrip = Path.Combine(wdb2FixtureRoot, "Complex-roundtrip.db2");
    complexTable.SaveAs(complexRoundTrip, false);
    if (!File.ReadAllBytes(complexRoundTrip).SequenceEqual(complexBytes))
        throw new InvalidOperationException("A complex WDB2 did not preserve its index map and copy table byte-for-byte.");
    try { complexTable.AddBlankRow(complexId); throw new InvalidOperationException("A complex WDB2 incorrectly allowed a structural edit."); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("side table", StringComparison.OrdinalIgnoreCase)) { }
    try { complexTable.SetRaw(0, complexId, 11); throw new InvalidOperationException("A complex WDB2 incorrectly allowed its indexed ID to change."); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("ID index map", StringComparison.OrdinalIgnoreCase)) { }

    var nestedTables = Path.Combine(wdb2FixtureRoot, "client", "DBFilesClient"); Directory.CreateDirectory(nestedTables); File.Copy(itemDb2, Path.Combine(nestedTables, "Item.db2"));
    var db2Audit = DbdSchemaService.Audit(wdb2FixtureRoot, nestedTables, 15595);
    if (db2Audit.Rows.Count != 1 || db2Audit.Matches != 1) throw new InvalidOperationException("The build-aware DBD audit did not validate a direct WDB2 table folder.");
    try { _ = DbdSchemaService.Audit(wdb2FixtureRoot, Path.Combine(wdb2FixtureRoot, "client"), 15595); throw new InvalidOperationException("A schema audit one directory too high incorrectly reported zero successful results."); }
    catch (InvalidDataException exception) when (exception.Message.Contains("DBFilesClient", StringComparison.OrdinalIgnoreCase)) { }
}
finally
{
    if (Directory.Exists(wdb2FixtureRoot)) Directory.Delete(wdb2FixtureRoot, true);
}
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
var translatedBounds = M2PreviewSceneService.TransformBounds(new Vector3(-1, -2, -3), new Vector3(1, 2, 3), Matrix4x4.CreateTranslation(10, 20, 30));
if (translatedBounds.Minimum != new Vector3(9, 18, 27) || translatedBounds.Maximum != new Vector3(11, 22, 33))
    throw new InvalidOperationException("Multi-model preview framing did not include transformed child-model bounds.");
var mapObjectTransform=M2PreviewSceneService.MapObjectTransform(new Vector3(0,0,90),0.5f,new Vector3(10,20,30));var transformedMapPoint=Vector3.Transform(new Vector3(2,0,0),mapObjectTransform);if(Vector3.Distance(transformedMapPoint,new Vector3(10,21,30))>0.0001f)throw new InvalidOperationException($"Map-object rotation/scale/translation transform was incorrect: {transformedMapPoint}.");
try { _ = M2PreviewSceneService.TransformBounds(Vector3.Zero, Vector3.One, new Matrix4x4(float.NaN,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1)); throw new InvalidOperationException("A non-finite preview-scene transform was accepted."); }
catch (ArgumentException exception) when (exception.Message.Contains("finite", StringComparison.OrdinalIgnoreCase)) { }
try { _ = M2PreviewSceneService.MapObjectTransform(Vector3.Zero,-1); throw new InvalidOperationException("A negative map-object scale was accepted."); }
catch (ArgumentException exception) when (exception.Message.Contains("scale", StringComparison.OrdinalIgnoreCase)) { }
if (M2PreviewSceneService.RecommendedAttachmentId(1) != 11 || M2PreviewSceneService.RecommendedAttachmentId(17) != 1 ||
    M2PreviewSceneService.RecommendedAttachmentId(22) != 2 || M2PreviewSceneService.RecommendedAttachmentId(27) != 30 ||
    M2PreviewSceneService.RecommendedAttachmentId(3) is not null)
    throw new InvalidOperationException("WotLK item inventory slots did not preserve the explicit character-attachment policy.");
var mountSourceFixture = Path.Combine(Path.GetTempPath(), $"crucible-mount-source-{Guid.NewGuid():N}");
try
{
    var sourceA = Path.Combine(mountSourceFixture, "patch-A"); var sourceB = Path.Combine(mountSourceFixture, "patch-B"); Directory.CreateDirectory(sourceA); Directory.CreateDirectory(sourceB);
    var modelA = Path.Combine(sourceA, "weapon.m2"); var modelB = Path.Combine(sourceB, "weapon.m2"); var textureA = Path.Combine(sourceA, "weapon.blp"); var textureB = Path.Combine(sourceB, "weapon.png");
    foreach (var path in new[] { modelA, modelB, textureA, textureB }) File.WriteAllBytes(path, [1]);
    var mountDisplay = thunderfuryDisplay with { Assets =
    [
        new ItemDisplayAsset("model", 0, "weapon.mdx", [], [modelA, modelB]),
        new ItemDisplayAsset("model-texture", 0, "weapon.blp", [], [textureB, textureA]),
        new ItemDisplayAsset("model-texture", 1, "wrong-slot.blp", [], [textureA])
    ] };
    var mountSources = M2PreviewSceneService.FindItemModelSources(mountDisplay);
    if (mountSources.Count != 2 || mountSources.Single(source => source.Provenance == "patch-A").TexturePath != textureA ||
        mountSources.Single(source => source.Provenance == "patch-B").TexturePath != textureB)
        throw new InvalidOperationException("Item-model mounting crossed provenance boundaries or mismatched ItemDisplayInfo model/texture slots.");
    var leftA = Path.Combine(sourceA, "shoulder-left.m2"); var rightA = Path.Combine(sourceA, "shoulder-right.m2"); var rightB = Path.Combine(sourceB, "shoulder-right.m2");
    var leftTextureA = Path.Combine(sourceA, "shoulder-left.blp"); var rightTextureA = Path.Combine(sourceA, "shoulder-right.blp"); var rightTextureB = Path.Combine(sourceB, "shoulder-right.blp");
    foreach (var path in new[] { leftA, rightA, rightB, leftTextureA, rightTextureA, rightTextureB }) File.WriteAllBytes(path, [2]);
    var shoulderDisplay = thunderfuryDisplay with { Assets =
    [
        new ItemDisplayAsset("model", 0, "shoulder-left.mdx", [], [leftA]),
        new ItemDisplayAsset("model", 1, "shoulder-right.mdx", [], [rightA, rightB]),
        new ItemDisplayAsset("model-texture", 0, "shoulder-left.blp", [], [leftTextureA]),
        new ItemDisplayAsset("model-texture", 1, "shoulder-right.blp", [], [rightTextureA, rightTextureB])
    ] };
    var completeShoulders = M2PreviewSceneService.PlanShoulderMounts(shoulderDisplay, leftA);
    var partialShoulders = M2PreviewSceneService.PlanShoulderMounts(shoulderDisplay, rightB);
    if (!completeShoulders.Complete || completeShoulders.Placements.Count != 2 || completeShoulders.Placements.Single(value => value.Source.ModelSlot == 0).AttachmentId != 6 || completeShoulders.Placements.Single(value => value.Source.ModelSlot == 1).AttachmentId != 5 ||
        partialShoulders.Complete || partialShoulders.Placements.Count != 1 || partialShoulders.Placements[0].Source.ModelPath != rightB || partialShoulders.Placements[0].AttachmentId != 5)
        throw new InvalidOperationException("Paired shoulder mounting did not preserve left/right native attachments or strict same-provenance selection.");
}
finally { if (Directory.Exists(mountSourceFixture)) Directory.Delete(mountSourceFixture, true); }
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

var knowledgeFixture = Path.Combine(Path.GetTempPath(), $"crucible-knowledge-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(knowledgeFixture, "docs", "es")); File.WriteAllText(Path.Combine(knowledgeFixture, "_config.yml"), "title: fixture");
File.WriteAllText(Path.Combine(knowledgeFixture, "docs", "item_template.md"), """
---
title: item\_template
---
# item_template
## Flags
The Flags field is a bitmask. Add ITEM_FLAG_BIND_TO_ACCOUNT to make a bound account item.
## RequiredLevel
Minimum player level required to equip the item.
""");
File.WriteAllText(Path.Combine(knowledgeFixture, "docs", "es", "item_template.md"), """
---
title: objeto
---
# objeto
## Flags
Referencia localizada para banderas.
""");
var knowledge = new KnowledgeReferenceService(); var knowledgeIndex = knowledge.Build(knowledgeFixture);
var flagHits = knowledge.Search("item_template flags", "en", 20); var prefixHits = knowledge.Search("bind_to_acc", null, 20); var localizedHits = knowledge.Search("banderas", "es", 20);
if (knowledgeIndex.Articles.Count != 2 || knowledgeIndex.SectionCount < 4 || !knowledgeIndex.Locales.SequenceEqual(["en", "es"]) ||
    flagHits.FirstOrDefault()?.Heading != "Flags" || flagHits[0].Title != "item_template" || !flagHits[0].PlainText.Contains("bitmask", StringComparison.OrdinalIgnoreCase) ||
    prefixHits.Count != 1 || localizedHits.Count != 1 || localizedHits[0].Locale != "es" || KnowledgeReferenceService.FindWikiRoot(Path.Combine(knowledgeFixture, "docs", "es")) != knowledgeFixture)
    throw new InvalidOperationException("Offline knowledge indexing, Markdown decoding, prefix search, locale filtering, or root discovery regressed.");
Directory.Delete(knowledgeFixture, true);

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
var mapWdt = Path.Combine(assetFixture, "fixture.wdt"); var mainFixture = new byte[64 * 64 * 8]; BitConverter.GetBytes((uint)1).CopyTo(mainFixture, 0); BitConverter.GetBytes((uint)3).CopyTo(mainFixture, (64 * 64 - 1) * 8);
using (var stream = File.Create(mapWdt)) using (var writer = new BinaryWriter(stream)) { WriteMapChunk(writer, "MVER", BitConverter.GetBytes((uint)18)); WriteMapChunk(writer, "MPHD", new byte[32]); WriteMapChunk(writer, "MAIN", mainFixture); }
var wdtInspection = MapAssetInspectionService.Inspect(mapWdt);
if (wdtInspection.Kind != MapAssetKind.Wdt || wdtInspection.Version != 18 || wdtInspection.PresentCells != 2 || !wdtInspection.Cells[0].Present || !wdtInspection.Cells[^1].Present || wdtInspection.GridWidth != 64)
    throw new InvalidOperationException("Native WDT MAIN tile inspection failed.");
var mapWdl = Path.Combine(assetFixture, "fixture.wdl"); var maofFixture = new byte[64 * 64 * 4]; BitConverter.GetBytes((uint)(12 + 8 + maofFixture.Length)).CopyTo(maofFixture, 5 * 4); var mareFixture = new byte[1090];
for (var height = 0; height < 545; height++) BitConverter.GetBytes((short)(height - 100)).CopyTo(mareFixture, height * 2);
using (var stream = File.Create(mapWdl)) using (var writer = new BinaryWriter(stream)) { WriteMapChunk(writer, "MVER", BitConverter.GetBytes((uint)18)); WriteMapChunk(writer, "MAOF", maofFixture); WriteMapChunk(writer, "MARE", mareFixture); }
var wdlInspection = MapAssetInspectionService.Inspect(mapWdl); var wdlCell = wdlInspection.Cells.Single(cell => cell.Present);
if (wdlInspection.Kind != MapAssetKind.Wdl || wdlInspection.PresentCells != 1 || wdlCell.X != 5 || wdlCell.Y != 0 || wdlCell.MinimumHeight != -100 || wdlCell.MaximumHeight != 444)
    throw new InvalidOperationException("Native WDL MAOF/MARE presence or horizon-height inspection failed.");
var mapAdt = Path.Combine(assetFixture, "fixture_31_33.adt"); var mcnkFixture = new byte[128 + 8 + 145 * 4 + 8 + 2 * 16 + 8 + 2048];
BitConverter.GetBytes((uint)3).CopyTo(mcnkFixture, 4); BitConverter.GetBytes((uint)4).CopyTo(mcnkFixture, 8); BitConverter.GetBytes((uint)2).CopyTo(mcnkFixture, 0x0C); BitConverter.GetBytes((uint)136).CopyTo(mcnkFixture, 0x14); BitConverter.GetBytes((uint)724).CopyTo(mcnkFixture, 0x1C); BitConverter.GetBytes((uint)764).CopyTo(mcnkFixture, 0x24); BitConverter.GetBytes((uint)2056).CopyTo(mcnkFixture, 0x28); BitConverter.GetBytes((uint)12).CopyTo(mcnkFixture, 0x34); BitConverter.GetBytes((uint)1).CopyTo(mcnkFixture, 0x3C); BitConverter.GetBytes(100f).CopyTo(mcnkFixture, 0x70);
System.Text.Encoding.ASCII.GetBytes("TVCM").CopyTo(mcnkFixture, 128); BitConverter.GetBytes((uint)(145 * 4)).CopyTo(mcnkFixture, 132); for (var height = 0; height < 145; height++) BitConverter.GetBytes((float)height).CopyTo(mcnkFixture, 136 + height * 4);
System.Text.Encoding.ASCII.GetBytes("YLCM").CopyTo(mcnkFixture, 716); BitConverter.GetBytes((uint)32).CopyTo(mcnkFixture, 720); BitConverter.GetBytes((uint)0).CopyTo(mcnkFixture, 724); BitConverter.GetBytes((uint)2).CopyTo(mcnkFixture, 740); BitConverter.GetBytes((uint)0x100).CopyTo(mcnkFixture, 744); BitConverter.GetBytes(7).CopyTo(mcnkFixture, 752);
System.Text.Encoding.ASCII.GetBytes("LACM").CopyTo(mcnkFixture, 756); BitConverter.GetBytes((uint)2048).CopyTo(mcnkFixture, 760);
var mtexFixture = System.Text.Encoding.UTF8.GetBytes("Textures\\GroundA.blp\0\0Textures\\GroundB.blp\0"); using (var stream = File.Create(mapAdt)) using (var writer = new BinaryWriter(stream)) { WriteMapChunk(writer, "MVER", BitConverter.GetBytes((uint)18)); WriteMapChunk(writer, "MTEX", mtexFixture); WriteMapChunk(writer, "MCNK", mcnkFixture); }
var adtInspection = MapAssetInspectionService.Inspect(mapAdt); var adtCell = adtInspection.Cells.Single(cell => cell.Present);
if (adtInspection.Kind != MapAssetKind.Adt || adtInspection.TileX != 31 || adtInspection.TileY != 33 || adtCell.X != 3 || adtCell.Y != 4 || adtCell.AreaId != 12 || adtCell.Holes != 1 || adtCell.MinimumHeight != 100f || adtCell.MaximumHeight != 244f || adtInspection.TexturePaths.Count != 2)
    throw new InvalidOperationException("Native ADT MCNK coordinate, area, holes, or MCVT height inspection failed.");
var textureInspection = AdtTextureLayerService.Inspect(mapAdt); var texturePlan = AdtTextureLayerService.Plan(mapAdt, [(3, 4)], 1, 0); var texturePreview = AdtTextureLayerService.Preview(texturePlan); var texturePlanPath = Path.Combine(assetFixture, "texture-layer-plan.json"); AdtTextureLayerService.SavePlan(texturePlan, texturePlanPath); var textureOutput = Path.Combine(assetFixture, "fixture-texture-edit.adt"); var textureResult = AdtTextureLayerService.Apply(AdtTextureLayerService.LoadPlan(texturePlanPath), textureOutput);
if (textureInspection.Textures.Count != 3 || textureInspection.Textures[1].Path.Length != 0 || textureInspection.Findings.Count != 1 || textureInspection.Layers.Count != 2 || textureInspection.Layers.Single(layer => layer.Slot == 1).TextureId != 2 || texturePreview.Layers.Single(layer => layer.Slot == 1).TextureId != 0 || AdtTextureLayerService.Inspect(mapAdt).Layers.Single(layer => layer.Slot == 1).TextureId != 2 || textureResult.Inspection.Layers.Single(layer => layer.Slot == 1).TextureId != 0 || !File.Exists(textureResult.ReceiptPath))
    throw new InvalidOperationException("Hash-bound ADT MCLY texture-layer inspection/preview/apply did not preserve the source or verify its output.");
try { _ = AdtTextureLayerService.Preview(texturePlan with { Edits = [texturePlan.Edits[0] with { TextureIdOffset = texturePlan.Edits[0].TextureIdOffset - 4 }] }); throw new InvalidOperationException("A redirected ADT texture-layer byte offset was accepted."); } catch (InvalidDataException) { }
var noOpTextureRejected = false; try { _ = AdtTextureLayerService.Plan(mapAdt, [(3, 4)], 1, 2); } catch (InvalidOperationException) { noOpTextureRejected = true; } if (!noOpTextureRejected) throw new InvalidOperationException("A byte-identical ADT texture-layer assignment was reported as an edit.");
var structuralAdt = Path.Combine(assetFixture, "fixture-structural_31_33.adt"); var mhdrFixture = new byte[64]; var mcinFixture = new byte[4096];
const int structuralMhdrOffset = 12; const int structuralMcinOffset = 84; const int structuralMtexOffset = 4188; var structuralMcnkOffset = structuralMtexOffset + 8 + mtexFixture.Length;
BitConverter.GetBytes((uint)(structuralMcinOffset - (structuralMhdrOffset + 8))).CopyTo(mhdrFixture, 4); BitConverter.GetBytes((uint)(structuralMtexOffset - (structuralMhdrOffset + 8))).CopyTo(mhdrFixture, 8);
var structuralMcinEntry = (4 * 16 + 3) * 16; BitConverter.GetBytes((uint)structuralMcnkOffset).CopyTo(mcinFixture, structuralMcinEntry); BitConverter.GetBytes((uint)(8 + mcnkFixture.Length)).CopyTo(mcinFixture, structuralMcinEntry + 4);
using (var stream = File.Create(structuralAdt)) using (var writer = new BinaryWriter(stream)) { WriteMapChunk(writer, "MVER", BitConverter.GetBytes((uint)18)); WriteMapChunk(writer, "MHDR", mhdrFixture); WriteMapChunk(writer, "MCIN", mcinFixture); WriteMapChunk(writer, "MTEX", mtexFixture); WriteMapChunk(writer, "MCNK", mcnkFixture); }
var structuralSourceBytes = File.ReadAllBytes(structuralAdt); var structuralPlan = AdtTextureStructureService.Plan(structuralAdt, @"Tileset\Crucible\NewGround.blp", [(3, 4)], AdtNewLayerEncoding.Packed4Bit, 0); var structuralPlanPath = Path.Combine(assetFixture, "texture-structure-plan.json"); AdtTextureStructureService.SavePlan(structuralPlan, structuralPlanPath); var structuralOutput = Path.Combine(assetFixture, "fixture-new-layer.adt"); var structuralResult = AdtTextureStructureService.Apply(AdtTextureStructureService.LoadPlan(structuralPlanPath), structuralOutput);
var structuralLayer = structuralResult.TextureInspection.Layers.Single(layer => layer.CellX == 3 && layer.CellY == 4 && layer.Slot == 2); var structuralAlpha = structuralResult.AlphaInspection.Maps.Single(map => map.CellX == 3 && map.CellY == 4 && map.Slot == 2);
if (structuralPlan.TextureId != 3 || structuralLayer.TextureId != 3 || structuralLayer.TexturePath != @"Tileset\Crucible\NewGround.blp" || structuralAlpha.Encoding != AdtAlphaEncoding.Packed4Bit || structuralAlpha.Minimum != 0 || structuralAlpha.Maximum != 0 || structuralResult.EditedCells != 1 || !File.Exists(structuralResult.ReceiptPath) || !File.ReadAllBytes(structuralAdt).SequenceEqual(structuralSourceBytes))
    throw new InvalidOperationException("Structural ADT MTEX/MCLY/MCAL insertion did not preserve the source or re-parse its appended catalog, layer, and alpha map.");
try { _ = AdtTextureStructureService.Plan(structuralAdt, @"Textures\GroundA.blp", [(3, 4)], AdtNewLayerEncoding.Packed4Bit); throw new InvalidOperationException("Structural ADT editing accepted a duplicate MTEX path."); } catch (InvalidOperationException exception) when (exception.Message.Contains("already contains", StringComparison.OrdinalIgnoreCase)) { }
try { _ = AdtTextureStructureService.Apply(structuralPlan with { Cells = [structuralPlan.Cells[0] with { OriginalMcnkSha256 = new string('0', 64) }] }, Path.Combine(assetFixture, "tampered-structure.adt")); throw new InvalidOperationException("Structural ADT editing accepted a changed MCNK preimage."); } catch (InvalidDataException) { }
var alphaInspection = AdtAlphaMapService.Inspect(mapAdt); var alphaPlan = AdtAlphaMapService.Plan(mapAdt, 1, 3.5f, 4.5f, 0.16f, 255, 0.75f, AdtTerrainBrushFalloff.Smooth, [(3, 4)]); var alphaPreview = AdtAlphaMapService.Preview(alphaPlan); var alphaPlanPath = Path.Combine(assetFixture, "alpha-plan.json"); AdtAlphaMapService.SavePlan(alphaPlan, alphaPlanPath); var alphaOutput = Path.Combine(assetFixture, "fixture-alpha-edit.adt"); var alphaResult = AdtAlphaMapService.Apply(AdtAlphaMapService.LoadPlan(alphaPlanPath), alphaOutput);
if (alphaInspection.Maps.Count != 1 || alphaInspection.Maps[0].Encoding != AdtAlphaEncoding.Packed4Bit || alphaInspection.Maps[0].Minimum != 0 || alphaInspection.Maps[0].Maximum != 0 || alphaPlan.Edits.Count != 1 || alphaPlan.Edits[0].ChangedPixels == 0 || alphaPreview.Maps[0].Maximum == 0 || AdtAlphaMapService.Inspect(mapAdt).Maps[0].Maximum != 0 || alphaResult.Inspection.Maps[0].Maximum == 0 || !File.Exists(alphaResult.ReceiptPath))
    throw new InvalidOperationException("Hash-bound fixed-width ADT MCAL alpha painting did not preserve the source or verify its output.");
try { _ = AdtAlphaMapService.Preview(alphaPlan with { Edits = [alphaPlan.Edits[0] with { DataOffset = alphaPlan.Edits[0].DataOffset - 1 }] }); throw new InvalidOperationException("A redirected ADT alpha-map byte offset was accepted."); } catch (InvalidDataException) { }
var codecPixels = Enumerable.Range(0, AdtAlphaMapCodec.PixelCount).Select(index => (byte)((index * 37) & 0xFF)).ToArray(); var bigEncoded = AdtAlphaMapCodec.Encode(AdtAlphaEncoding.Big8Bit, codecPixels); var rleEncoded = AdtAlphaMapCodec.Encode(AdtAlphaEncoding.Rle8Bit, codecPixels);
if (!AdtAlphaMapCodec.Decode(AdtAlphaEncoding.Big8Bit, bigEncoded).SequenceEqual(codecPixels) || !AdtAlphaMapCodec.Decode(AdtAlphaEncoding.Rle8Bit, rleEncoded).SequenceEqual(codecPixels)) throw new InvalidOperationException("Big or RLE MCAL codec round-trip failed.");
var packedEncoded = AdtAlphaMapCodec.Encode(AdtAlphaEncoding.Packed4Bit, codecPixels); var packedDecoded = AdtAlphaMapCodec.Decode(AdtAlphaEncoding.Packed4Bit, packedEncoded);
if (packedEncoded.Length != 2048 || packedDecoded[63] != packedDecoded[62] || !packedDecoded.AsSpan(63 * 64, 64).SequenceEqual(packedDecoded.AsSpan(62 * 64, 64))) throw new InvalidOperationException("Packed 4-bit MCAL edge semantics failed.");
var rleAdt = Path.Combine(assetFixture, "fixture-rle_31_33.adt"); var zeroRle = AdtAlphaMapCodec.Encode(AdtAlphaEncoding.Rle8Bit, new byte[AdtAlphaMapCodec.PixelCount]); var rleMcnk = new byte[756 + 8 + zeroRle.Length]; Array.Copy(mcnkFixture, rleMcnk, 756); BitConverter.GetBytes((uint)(zeroRle.Length + 8)).CopyTo(rleMcnk, 0x28); BitConverter.GetBytes((uint)0x300).CopyTo(rleMcnk, 744); System.Text.Encoding.ASCII.GetBytes("LACM").CopyTo(rleMcnk, 756); BitConverter.GetBytes((uint)zeroRle.Length).CopyTo(rleMcnk, 760); zeroRle.CopyTo(rleMcnk, 764);
using (var stream = File.Create(rleAdt)) using (var writer = new BinaryWriter(stream)) { WriteMapChunk(writer, "MVER", BitConverter.GetBytes((uint)18)); WriteMapChunk(writer, "MTEX", mtexFixture); WriteMapChunk(writer, "MCNK", rleMcnk); }
var rlePlan = AdtAlphaMapService.Plan(rleAdt, 1, 3.5f, 4.5f, 1f, 255, 1f, AdtTerrainBrushFalloff.Constant); var rleOutput = Path.Combine(assetFixture, "fixture-rle-alpha.adt"); var rleResult = AdtAlphaMapService.Apply(rlePlan, rleOutput); var rleWritten = rleResult.Inspection.Maps.Single();
if (rleWritten.Encoding != AdtAlphaEncoding.Rle8Bit || rleWritten.Capacity != zeroRle.Length || rleWritten.EncodedBytesUsed != zeroRle.Length || rleWritten.Minimum != 255 || rleWritten.Maximum != 255 || rleResult.EditedPixels != AdtAlphaMapCodec.PixelCount) throw new InvalidOperationException("Fixed-capacity RLE MCAL planning/apply failed.");
var heightPlan = AdtHeightEditService.Plan(mapAdt, [(3, 4)], 10f); var heightPreview = AdtHeightEditService.Preview(heightPlan); var heightPreviewCell = heightPreview.Cells.Single(cell => cell.Present);
var heightPlanPath = Path.Combine(assetFixture, "height-plan.json"); AdtHeightEditService.SavePlan(heightPlan, heightPlanPath); var loadedHeightPlan = AdtHeightEditService.LoadPlan(heightPlanPath); var editedAdt = Path.Combine(assetFixture, "fixture-height-edit.adt");
var heightResult = AdtHeightEditService.Apply(loadedHeightPlan, editedAdt); var originalAdtCell = MapAssetInspectionService.Inspect(mapAdt).Cells.Single(cell => cell.Present); var writtenAdtCell = heightResult.Inspection.Cells.Single(cell => cell.Present);
if (heightPreviewCell.MinimumHeight != 110f || heightPreviewCell.MaximumHeight != 254f || originalAdtCell.MinimumHeight != 100f || writtenAdtCell.MinimumHeight != 110f || writtenAdtCell.MaximumHeight != 254f || heightResult.EditedCells != 1 || !File.Exists(heightResult.ReceiptPath))
    throw new InvalidOperationException("Hash-bound ADT terrain-height preview/apply did not preserve the source or verify the edited output.");
var brushPlan = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 20f, AdtTerrainBrushFalloff.Linear); var brushPreview = AdtTerrainBrushService.Preview(brushPlan); var brushPreviewCell = brushPreview.Cells.Single(cell => cell.Present);
var brushPlanPath = Path.Combine(assetFixture, "brush-plan.json"); AdtTerrainBrushService.SavePlan(brushPlan, brushPlanPath); var loadedBrushPlan = AdtTerrainBrushService.LoadPlan(brushPlanPath); var brushedAdt = Path.Combine(assetFixture, "fixture-brushed.adt"); var brushResult = AdtTerrainBrushService.Apply(loadedBrushPlan, brushedAdt);
var brushedCell = brushResult.Inspection.Cells.Single(cell => cell.Present); var lastVertex = AdtTerrainBrushService.VertexPosition(144); var innerVertex = AdtTerrainBrushService.VertexPosition(9);
if (brushPlan.Vertices.Count != 1 || brushPlan.Vertices[0].VertexIndex != 144 || brushPreviewCell.MaximumHeight != 264f || originalAdtCell.MaximumHeight != 244f || brushedCell.MaximumHeight != 264f || brushResult.EditedVertices != 1 || brushResult.EditedCells != 1 || !File.Exists(brushResult.ReceiptPath) || lastVertex != (1f, 1f) || innerVertex != (0.0625f, 0.0625f))
    throw new InvalidOperationException("Hash-bound ADT MCVT vertex brush did not preserve the source, map the staggered grid, or verify its edited output.");
try { _ = AdtTerrainBrushService.Preview(brushPlan with { Vertices = [brushPlan.Vertices[0] with { HeightOffset = brushPlan.Vertices[0].HeightOffset - 4 }] }); throw new InvalidOperationException("A redirected terrain-brush byte offset was accepted."); }
catch (InvalidDataException) { }
var flattenPlan = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 10f, AdtTerrainBrushFalloff.Constant, AdtTerrainBrushMode.Flatten, 200f); var smoothPlan = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 100f, AdtTerrainBrushFalloff.Constant, AdtTerrainBrushMode.Smooth);
var noisePlanA = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 10f, AdtTerrainBrushFalloff.Constant, AdtTerrainBrushMode.Noise, seed: 42); var noisePlanB = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 10f, AdtTerrainBrushFalloff.Constant, AdtTerrainBrushMode.Noise, seed: 42); var noisePlanC = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 10f, AdtTerrainBrushFalloff.Constant, AdtTerrainBrushMode.Noise, seed: 43);
if (flattenPlan.Vertices.Single().EditedRelativeHeight != 134f || smoothPlan.Vertices.Single().EditedRelativeHeight >= 144f || noisePlanA.Vertices.Single().EditedRelativeHeight != noisePlanB.Vertices.Single().EditedRelativeHeight || noisePlanA.Vertices.Single().EditedRelativeHeight == noisePlanC.Vertices.Single().EditedRelativeHeight)
    throw new InvalidOperationException("Flatten, source-neighbor smooth, or deterministic seeded-noise terrain-brush math failed.");
var legacyBrushJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(brushPlanPath))!.AsObject(); legacyBrushJson.Remove("Mode"); legacyBrushJson.Remove("TargetHeight"); legacyBrushJson.Remove("Seed"); var legacyBrushPath = Path.Combine(assetFixture, "brush-plan-v1-original.json"); File.WriteAllText(legacyBrushPath, legacyBrushJson.ToJsonString());
if (AdtTerrainBrushService.LoadPlan(legacyBrushPath).Mode != AdtTerrainBrushMode.RaiseLower) throw new InvalidOperationException("Original version-1 raise/lower terrain-brush plans lost backward compatibility.");
try { _ = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 10f, AdtTerrainBrushFalloff.Constant, AdtTerrainBrushMode.Flatten); throw new InvalidOperationException("Flatten terrain brush accepted a missing target height."); }
catch (ArgumentOutOfRangeException) { }
var noOpBrushRejected = false; try { _ = AdtTerrainBrushService.Plan(mapAdt, 4f, 5f, 0.04f, 10f, AdtTerrainBrushFalloff.Constant, AdtTerrainBrushMode.Flatten, 244f); } catch (InvalidOperationException) { noOpBrushRejected = true; }
if (!noOpBrushRejected) throw new InvalidOperationException("A byte-identical flatten stroke was reported as an edit.");
var geometryModelPath = Path.Combine(assetFixture, "geometry.m2"); var geometryBytes = new byte[0x300];
System.Text.Encoding.ASCII.GetBytes("MD20").CopyTo(geometryBytes, 0); BitConverter.GetBytes((uint)264).CopyTo(geometryBytes, 4); BitConverter.GetBytes((uint)3).CopyTo(geometryBytes, 0x3C); BitConverter.GetBytes((uint)0x130).CopyTo(geometryBytes, 0x40);
const int fixtureBoneOffset = 0x220; const int fixtureAttachmentOffset = 0x278; const int fixtureAttachmentLookupOffset = 0x2A0;
const int fixtureRenderFlagOffset = 0x2C0; BitConverter.GetBytes((uint)1).CopyTo(geometryBytes, 0x70); BitConverter.GetBytes((uint)fixtureRenderFlagOffset).CopyTo(geometryBytes, 0x74); BitConverter.GetBytes((ushort)1).CopyTo(geometryBytes, fixtureRenderFlagOffset); BitConverter.GetBytes((ushort)2).CopyTo(geometryBytes, fixtureRenderFlagOffset + 2);
BitConverter.GetBytes((uint)1).CopyTo(geometryBytes, 0x2C); BitConverter.GetBytes((uint)fixtureBoneOffset).CopyTo(geometryBytes, 0x30); BitConverter.GetBytes((short)-1).CopyTo(geometryBytes, fixtureBoneOffset + 8); BitConverter.GetBytes(1.25f).CopyTo(geometryBytes, fixtureBoneOffset + 84);
BitConverter.GetBytes((uint)1).CopyTo(geometryBytes, 0xF0); BitConverter.GetBytes((uint)fixtureAttachmentOffset).CopyTo(geometryBytes, 0xF4); BitConverter.GetBytes((uint)12).CopyTo(geometryBytes, 0xF8); BitConverter.GetBytes((uint)fixtureAttachmentLookupOffset).CopyTo(geometryBytes, 0xFC);
BitConverter.GetBytes((uint)11).CopyTo(geometryBytes, fixtureAttachmentOffset); BitConverter.GetBytes((uint)0).CopyTo(geometryBytes, fixtureAttachmentOffset + 4); BitConverter.GetBytes(0.25f).CopyTo(geometryBytes, fixtureAttachmentOffset + 8); BitConverter.GetBytes(-0.5f).CopyTo(geometryBytes, fixtureAttachmentOffset + 12); BitConverter.GetBytes(1.25f).CopyTo(geometryBytes, fixtureAttachmentOffset + 16);
for (var index = 0; index < 12; index++) BitConverter.GetBytes((short)-1).CopyTo(geometryBytes, fixtureAttachmentLookupOffset + index * 2); BitConverter.GetBytes((short)0).CopyTo(geometryBytes, fixtureAttachmentLookupOffset + 11 * 2);
var textureDefinitionOffset = 0x130 + 3 * 48; BitConverter.GetBytes((uint)1).CopyTo(geometryBytes, 0x50); BitConverter.GetBytes((uint)textureDefinitionOffset).CopyTo(geometryBytes, 0x54);
const int textureLookupOffset = 0x2D0, textureCoordinateLookupOffset = 0x2D4, transparencyLookupOffset = 0x2D8, textureAnimationLookupOffset = 0x2DA;
BitConverter.GetBytes((uint)2).CopyTo(geometryBytes, 0x80); BitConverter.GetBytes((uint)textureLookupOffset).CopyTo(geometryBytes, 0x84); BitConverter.GetBytes((ushort)0).CopyTo(geometryBytes, textureLookupOffset); BitConverter.GetBytes((ushort)0).CopyTo(geometryBytes, textureLookupOffset + 2);
BitConverter.GetBytes((uint)2).CopyTo(geometryBytes, 0x88); BitConverter.GetBytes((uint)textureCoordinateLookupOffset).CopyTo(geometryBytes, 0x8C); BitConverter.GetBytes((short)0).CopyTo(geometryBytes, textureCoordinateLookupOffset); BitConverter.GetBytes((short)1).CopyTo(geometryBytes, textureCoordinateLookupOffset + 2);
BitConverter.GetBytes((uint)1).CopyTo(geometryBytes, 0x90); BitConverter.GetBytes((uint)transparencyLookupOffset).CopyTo(geometryBytes, 0x94); BitConverter.GetBytes((ushort)0).CopyTo(geometryBytes, transparencyLookupOffset);
BitConverter.GetBytes((uint)2).CopyTo(geometryBytes, 0x98); BitConverter.GetBytes((uint)textureAnimationLookupOffset).CopyTo(geometryBytes, 0x9C); BitConverter.GetBytes((ushort)0).CopyTo(geometryBytes, textureAnimationLookupOffset); BitConverter.GetBytes(ushort.MaxValue).CopyTo(geometryBytes, textureAnimationLookupOffset + 2);
var embeddedFixturePath = @"Character\BloodElf\Female\fixture.blp"; var embeddedFixtureBytes = System.Text.Encoding.ASCII.GetBytes(embeddedFixturePath + "\0");
BitConverter.GetBytes((uint)0).CopyTo(geometryBytes, textureDefinitionOffset); BitConverter.GetBytes((uint)3).CopyTo(geometryBytes, textureDefinitionOffset + 4); BitConverter.GetBytes((uint)embeddedFixtureBytes.Length).CopyTo(geometryBytes, textureDefinitionOffset + 8); BitConverter.GetBytes((uint)(textureDefinitionOffset + 16)).CopyTo(geometryBytes, textureDefinitionOffset + 12); embeddedFixtureBytes.CopyTo(geometryBytes, textureDefinitionOffset + 16);
var fixtureVertices = new[] { (X: -1f, Y: 0f, Z: -1f), (X: 1f, Y: 0f, Z: -1f), (X: 0f, Y: 0f, Z: 1f) };
for (var index = 0; index < fixtureVertices.Length; index++)
{
    var offset = 0x130 + index * 48; BitConverter.GetBytes(fixtureVertices[index].X).CopyTo(geometryBytes, offset); BitConverter.GetBytes(fixtureVertices[index].Y).CopyTo(geometryBytes, offset + 4); BitConverter.GetBytes(fixtureVertices[index].Z).CopyTo(geometryBytes, offset + 8); BitConverter.GetBytes(1f).CopyTo(geometryBytes, offset + 28); BitConverter.GetBytes(index / 2f).CopyTo(geometryBytes, offset + 40); BitConverter.GetBytes(1f - index / 2f).CopyTo(geometryBytes, offset + 44);
}
File.WriteAllBytes(geometryModelPath, geometryBytes);
var geometrySkin = new byte[240]; System.Text.Encoding.ASCII.GetBytes("SKIN").CopyTo(geometrySkin, 0); BitConverter.GetBytes((uint)3).CopyTo(geometrySkin, 4); BitConverter.GetBytes((uint)48).CopyTo(geometrySkin, 8); BitConverter.GetBytes((uint)9).CopyTo(geometrySkin, 12); BitConverter.GetBytes((uint)54).CopyTo(geometrySkin, 16); BitConverter.GetBytes((uint)3).CopyTo(geometrySkin, 28); BitConverter.GetBytes((uint)72).CopyTo(geometrySkin, 32); BitConverter.GetBytes((uint)1).CopyTo(geometrySkin, 36); BitConverter.GetBytes((uint)216).CopyTo(geometrySkin, 40);
for (ushort index = 0; index < 3; index++) BitConverter.GetBytes(index).CopyTo(geometrySkin, 48 + index * 2);
for (ushort index = 0; index < 9; index++) BitConverter.GetBytes((ushort)(index % 3)).CopyTo(geometrySkin, 54 + index * 2);
BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 72); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 78); BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 80); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 82);
BitConverter.GetBytes((ushort)2).CopyTo(geometrySkin, 120); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 126); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 128); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 130);
BitConverter.GetBytes((ushort)702).CopyTo(geometrySkin, 168); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 174); BitConverter.GetBytes((ushort)6).CopyTo(geometrySkin, 176); BitConverter.GetBytes((ushort)3).CopyTo(geometrySkin, 178);
geometrySkin[216] = 16; BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 220); BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 222); BitConverter.GetBytes((short)-1).CopyTo(geometrySkin, 224); BitConverter.GetBytes((ushort)2).CopyTo(geometrySkin, 230); BitConverter.GetBytes((ushort)0).CopyTo(geometrySkin, 232);
File.WriteAllBytes(Path.Combine(assetFixture, "geometry00.skin"), geometrySkin);
var previewGeometry = M2PreviewGeometryService.Load(geometryModelPath);
var relativeSkinGeometry = M2PreviewGeometryService.Load(geometryModelPath, "geometry00.skin");
if (!Path.GetFullPath(relativeSkinGeometry.SkinPath).Equals(Path.GetFullPath(Path.Combine(assetFixture, "geometry00.skin")), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("A relative explicit SKIN filename was not resolved beside its M2 model.");
var environmentModelPath = Path.Combine(assetFixture, "geometry-environment.m2"); var environmentBytes = geometryBytes.ToArray(); BitConverter.GetBytes((short)-1).CopyTo(environmentBytes, textureCoordinateLookupOffset + 2); File.WriteAllBytes(environmentModelPath, environmentBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-environment00.skin"));
var environmentGeometry = M2PreviewGeometryService.Load(environmentModelPath);
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
var legacyAddCombiner = M2PreviewGeometryService.DescribeCombiner(3, 2); var legacyMod2xCombiner = M2PreviewGeometryService.DescribeCombiner(0x74, 2); var explicitMod2x = M2PreviewGeometryService.DescribeCombiner(0x8000, 2); var explicitAdd = M2PreviewGeometryService.DescribeCombiner(0x8001, 2); var explicitAddAlphaAlpha = M2PreviewGeometryService.DescribeCombiner(0x8002, 2); var explicitAddPrimary = M2PreviewGeometryService.DescribeCombiner(0x8005, 2); var explicitModAdd = M2PreviewGeometryService.DescribeCombiner(0x8006, 2); var explicitEdgeFade = M2PreviewGeometryService.DescribeCombiner(0x8015, 2); var explicitOpaqueAlpha = M2PreviewGeometryService.DescribeCombiner(0x8017, 2); var unsupportedExplicit = M2PreviewGeometryService.DescribeCombiner(0x8004, 2); var unsupportedExplicitStageCount = M2PreviewGeometryService.DescribeCombiner(0x8001, 1);
if (legacyAddCombiner.Name != "Opaque_Add" || !legacyAddCombiner.Supported || legacyAddCombiner.Exact || legacyMod2xCombiner.Name != "Mod_Mod2x" || legacyMod2xCombiner.Exact || !explicitMod2x.Supported || explicitMod2x.Exact || explicitMod2x.Kind != M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlpha || !explicitAdd.Supported || explicitAdd.Kind != M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlpha || explicitAddAlphaAlpha.Kind != M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlphaAlpha || explicitAddPrimary.Kind != M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlphaPrimary || !explicitModAdd.Supported || explicitModAdd.Kind != M2PreviewTextureCombinerKind.ExplicitModAddAlpha || explicitEdgeFade.Kind != M2PreviewTextureCombinerKind.ExplicitModModEdgeFade || explicitOpaqueAlpha.Kind != M2PreviewTextureCombinerKind.ExplicitOpaqueAlpha || unsupportedExplicit.Supported || unsupportedExplicitStageCount.Supported)
    throw new InvalidOperationException("Wrath M2 material combiner decoding did not distinguish supported legacy/explicit families from unsupported explicit shader paths.");
var explicitShaderModelPath = Path.Combine(assetFixture, "geometry-explicit-shader.m2"); File.WriteAllBytes(explicitShaderModelPath, geometryBytes); var explicitShaderSkin = geometrySkin.ToArray(); BitConverter.GetBytes((ushort)0x8001).CopyTo(explicitShaderSkin, 218); File.WriteAllBytes(Path.Combine(assetFixture, "geometry-explicit-shader00.skin"), explicitShaderSkin); var explicitShaderGeometry = M2PreviewGeometryService.Load(explicitShaderModelPath); var explicitShaderMaterial = explicitShaderGeometry.MaterialUnits.Single();
if (explicitShaderMaterial.Combiner.Kind != M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlpha || !explicitShaderMaterial.Combiner.Supported || explicitShaderMaterial.TextureStages[0].CoordinateSource != M2PreviewTextureCoordinateSource.Primary || explicitShaderMaterial.TextureStages[1].CoordinateSource != M2PreviewTextureCoordinateSource.Environment || explicitShaderMaterial.TextureStages[1].Blend != M2PreviewTextureStageBlend.Add)
    throw new InvalidOperationException("Explicit Opaque_AddAlpha shader parsing did not enforce its native primary/environment routes and additive-alpha stage.");
var explicitAddPlan = M2TextureCombinerRenderPlanService.Build(explicitShaderMaterial.Combiner, explicitShaderMaterial.TextureStages); var explicitMod2xPlan = M2TextureCombinerRenderPlanService.Build(explicitMod2x, explicitShaderMaterial.TextureStages);
if (!explicitAddPlan.SequenceEqual([new M2TextureRenderPass(0, M2TextureRenderPassBlend.Source, true), new M2TextureRenderPass(1, M2TextureRenderPassBlend.Add, false)]) || !explicitMod2xPlan.SequenceEqual([new M2TextureRenderPass(0, M2TextureRenderPassBlend.Source, true), new M2TextureRenderPass(1, M2TextureRenderPassBlend.Modulate, false), new M2TextureRenderPass(0, M2TextureRenderPassBlend.DestinationOut, false), new M2TextureRenderPass(0, M2TextureRenderPassBlend.Add, true)]))
    throw new InvalidOperationException("Explicit build-264 shader render plans lost their validated additive-alpha or alpha-weighted Mod2xNA pass order.");
foreach (var route in new[]
{
    (Stem: "shader-2", Shader: (ushort)0x8002, Kind: M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlphaAlpha, Sources: new[] { M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureCoordinateSource.Environment }, Blends: new[] { M2PreviewTextureStageBlend.Source, M2PreviewTextureStageBlend.Add }, Plan: new[] { new M2TextureRenderPass(0, M2TextureRenderPassBlend.Source, true), new M2TextureRenderPass(1, M2TextureRenderPassBlend.DestinationOver, false) }),
    (Stem: "shader-5", Shader: (ushort)0x8005, Kind: M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlphaPrimary, Sources: new[] { M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureCoordinateSource.Primary }, Blends: new[] { M2PreviewTextureStageBlend.Source, M2PreviewTextureStageBlend.Add }, Plan: new[] { new M2TextureRenderPass(0, M2TextureRenderPassBlend.Source, true), new M2TextureRenderPass(1, M2TextureRenderPassBlend.Add, false) }),
    (Stem: "shader-21", Shader: (ushort)0x8015, Kind: M2PreviewTextureCombinerKind.ExplicitModModEdgeFade, Sources: new[] { M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureCoordinateSource.Secondary }, Blends: new[] { M2PreviewTextureStageBlend.Source, M2PreviewTextureStageBlend.Modulate }, Plan: new[] { new M2TextureRenderPass(0, M2TextureRenderPassBlend.Source, true, true), new M2TextureRenderPass(1, M2TextureRenderPassBlend.Modulate, false) }),
    (Stem: "shader-23", Shader: (ushort)0x8017, Kind: M2PreviewTextureCombinerKind.ExplicitOpaqueAlpha, Sources: new[] { M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureCoordinateSource.Primary }, Blends: new[] { M2PreviewTextureStageBlend.Source, M2PreviewTextureStageBlend.Source }, Plan: new[] { new M2TextureRenderPass(0, M2TextureRenderPassBlend.Source, true), new M2TextureRenderPass(1, M2TextureRenderPassBlend.Source, true) })
})
{
    var modelPath = Path.Combine(assetFixture, $"geometry-explicit-{route.Stem}.m2"); File.WriteAllBytes(modelPath, geometryBytes);
    var skinBytes = geometrySkin.ToArray(); BitConverter.GetBytes(route.Shader).CopyTo(skinBytes, 218); File.WriteAllBytes(Path.Combine(assetFixture, $"geometry-explicit-{route.Stem}00.skin"), skinBytes);
    var material = M2PreviewGeometryService.Load(modelPath).MaterialUnits.Single();
    if (!material.Combiner.Supported || material.Combiner.Exact || material.Combiner.Kind != route.Kind || !material.TextureStages.Select(stage => stage.CoordinateSource).SequenceEqual(route.Sources) || !material.TextureStages.Select(stage => stage.Blend).SequenceEqual(route.Blends) || !M2TextureCombinerRenderPlanService.Build(material.Combiner, material.TextureStages).SequenceEqual(route.Plan))
        throw new InvalidOperationException($"Explicit build-264 {route.Stem} UV routing, stage semantics, or render-pass reconstruction failed.");
}
try { _ = M2TextureCombinerRenderPlanService.Build(unsupportedExplicit, explicitShaderMaterial.TextureStages); throw new InvalidOperationException("An unsupported explicit shader received a render plan."); } catch (NotSupportedException) { }
var explicitThreeModelPath = Path.Combine(assetFixture, "geometry-explicit-shader-three.m2"); var explicitThreeBytes = new byte[0x400]; geometryBytes.CopyTo(explicitThreeBytes, 0); BitConverter.GetBytes((uint)3).CopyTo(explicitThreeBytes, 0x80); BitConverter.GetBytes((uint)0x300).CopyTo(explicitThreeBytes, 0x84); BitConverter.GetBytes((ushort)0).CopyTo(explicitThreeBytes, 0x300); BitConverter.GetBytes((ushort)0).CopyTo(explicitThreeBytes, 0x302); BitConverter.GetBytes((ushort)0).CopyTo(explicitThreeBytes, 0x304); BitConverter.GetBytes((uint)3).CopyTo(explicitThreeBytes, 0x88); BitConverter.GetBytes((uint)0x306).CopyTo(explicitThreeBytes, 0x8C); BitConverter.GetBytes((short)0).CopyTo(explicitThreeBytes, 0x306); BitConverter.GetBytes((short)-1).CopyTo(explicitThreeBytes, 0x308); BitConverter.GetBytes((short)0).CopyTo(explicitThreeBytes, 0x30A); File.WriteAllBytes(explicitThreeModelPath, explicitThreeBytes);
var explicitThreeSkin = geometrySkin.ToArray(); BitConverter.GetBytes((ushort)0x8003).CopyTo(explicitThreeSkin, 218); BitConverter.GetBytes((ushort)3).CopyTo(explicitThreeSkin, 230); File.WriteAllBytes(Path.Combine(assetFixture, "geometry-explicit-shader-three00.skin"), explicitThreeSkin); var explicitThreeMaterial = M2PreviewGeometryService.Load(explicitThreeModelPath).MaterialUnits.Single(); var explicitThreePlan = M2TextureCombinerRenderPlanService.Build(explicitThreeMaterial.Combiner, explicitThreeMaterial.TextureStages);
if (explicitThreeMaterial.Combiner.Kind != M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlphaAdd || !explicitThreeMaterial.Combiner.Supported || !explicitThreeMaterial.TextureStages.Select(stage => stage.CoordinateSource).SequenceEqual([M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureCoordinateSource.Environment, M2PreviewTextureCoordinateSource.Primary]) || !explicitThreeMaterial.TextureStages.Select(stage => stage.Blend).SequenceEqual([M2PreviewTextureStageBlend.Source, M2PreviewTextureStageBlend.Modulate2X, M2PreviewTextureStageBlend.Add]) || !explicitThreePlan.SequenceEqual([new M2TextureRenderPass(0, M2TextureRenderPassBlend.Source, true), new M2TextureRenderPass(1, M2TextureRenderPassBlend.Modulate, false), new M2TextureRenderPass(0, M2TextureRenderPassBlend.DestinationOut, false), new M2TextureRenderPass(0, M2TextureRenderPassBlend.Add, true), new M2TextureRenderPass(2, M2TextureRenderPassBlend.Add, false)]))
    throw new InvalidOperationException("Explicit three-stage Opaque_Mod2xNA_Alpha_Add parsing or render-pass reconstruction failed.");
var sphereCenter = M2EnvironmentMapService.Coordinate(Vector3.UnitY); var sphereRight = M2EnvironmentMapService.Coordinate(Vector3.Normalize(new Vector3(1, -1, 0))); var sphereLeft = M2EnvironmentMapService.Coordinate(Vector3.Normalize(new Vector3(-1, -1, 0)));
if (Vector2.Distance(sphereCenter, new Vector2(0.5f)) > 0.0001f || sphereRight.X <= 0.5f || sphereLeft.X >= 0.5f || sphereRight.X > 1 || sphereLeft.X < 0 || environmentGeometry.MaterialUnits[0].TextureStages[1].CoordinateSource != M2PreviewTextureCoordinateSource.Environment || !environmentGeometry.MaterialUnits[0].Combiner.Supported)
    throw new InvalidOperationException("Fixed-function M2 environment/sphere-map decoding or view-space coordinates regressed.");
var faceOnOpacity = M2EdgeFadeService.Opacity(Vector3.UnitY, -Vector3.UnitY); var silhouetteOpacity = M2EdgeFadeService.Opacity(Vector3.UnitX, Vector3.UnitY); var diagonalOpacity = M2EdgeFadeService.Opacity(Vector3.Normalize(new Vector3(1, 1, 0)), Vector3.UnitY);
if (Math.Abs(faceOnOpacity - 1f) > 0.0001f || silhouetteOpacity > 0.0001f || Math.Abs(diagonalOpacity - MathF.Sqrt(0.5f)) > 0.0001f || M2EdgeFadeService.Opacity(Vector3.Zero, Vector3.UnitY) != 1f)
    throw new InvalidOperationException("Linear view-angle M2 edge-fade opacity math regressed.");
if (previewGeometry.Vertices.Count != 3 || previewGeometry.TriangleIndices.Count != 6 || previewGeometry.TotalTriangleIndices != 9 || previewGeometry.Submeshes.Count != 3 || previewGeometry.Submeshes.Count(section => section.Visible) != 2 || !previewGeometry.Submeshes.Single(section => section.GeosetId == 702).Visible ||
    allGeosetGeometry.TriangleIndices.Count != 9 || allGeosetGeometry.Submeshes.Count(section => section.Visible) != 3 || previewGeometry.Minimum.X != -1f || previewGeometry.Maximum.Z != 1f || previewGeometry.TextureSlots.Count != 1 || previewGeometry.TextureSlots[0].EmbeddedPath != embeddedFixturePath || previewGeometry.TextureSlots[0].Flags != 3 ||
    previewGeometry.RenderFlags.Count != 1 || !previewGeometry.RenderFlags[0].Unlit || previewGeometry.RenderFlags[0].BlendMode != 2 || previewGeometry.MaterialUnits.Count != 1 || previewGeometry.MaterialUnits[0].SubmeshIndex != 0 || previewGeometry.MaterialUnits[0].TextureDefinitionIndex != 0 || previewGeometry.MaterialUnits[0].TextureStages.Count != 2 || previewGeometry.MaterialUnits[0].TextureStages[1].CoordinateSource != M2PreviewTextureCoordinateSource.Secondary || previewGeometry.MaterialUnits[0].TextureStages[1].Blend != M2PreviewTextureStageBlend.Modulate || previewGeometry.MaterialUnits[0].Combiner.Name != "Opaque_Opaque" || !previewGeometry.MaterialUnits[0].Combiner.Supported || previewGeometry.MaterialUnits[0].Combiner.Exact || previewGeometry.SecondaryTextureCoordinates[2] != new Vector2(1f, 0f) || previewGeometry.Batches.Count != 2 || previewGeometry.Batches[0].TextureDefinitionIndex != 0 || previewGeometry.Batches[0].TextureStages.Count != 2 || previewGeometry.Batches[0].BlendMode != 2 ||
    selectedHairGeometry.TriangleIndices.Count != 9 || selectedHairGeometry.Submeshes.Count(section => section.Visible) != 3 || selectedHairGeometry.GeosetSelection?.GroupVariants[0] != 2 ||
    nakedGeometry.TriangleIndices.Count != 6 || nakedGeometry.Submeshes.Count(section => section.Visible) != 2 || nakedGeometry.GeosetSelection?.GroupVariants[0] != 0 ||
    previewGeometry.Bones.Count != 1 || previewGeometry.Bones[0].ParentIndex != -1 || previewGeometry.Bones[0].Pivot.Z != 1.25f ||
    previewGeometry.Attachments.Count != 1 || previewGeometry.Attachments[0].Id != 11 || previewGeometry.Attachments[0].Name != "Helmet" || previewGeometry.Attachments[0].BoneIndex != 0 || previewGeometry.Attachments[0].Position.Y != -0.5f || !previewGeometry.Attachments[0].LookupSlots.SequenceEqual([11]))
    throw new InvalidOperationException("Native M2/SKIN preview geometry parsing failed.");
var animatedModel = Path.Combine(assetFixture, "geometry-animated.m2"); var animatedBytes = new byte[0x900]; geometryBytes.CopyTo(animatedBytes, 0);
const int fixtureSequenceOffset = 0x300, fixtureTimeSeriesOffset = 0x340, fixtureValueSeriesOffset = 0x348, fixtureTimesOffset = 0x350, fixtureValuesOffset = 0x358;
const int fixtureCameraOffset = 0x500, fixtureCameraLookupOffset = 0x564, fixtureLightOffset = 0x568, fixtureScalarSeriesOffset = 0x610, fixtureScalarValuesOffset = 0x620, fixtureRollSeriesOffset = 0x630, fixtureRollValuesOffset = 0x638, fixtureIntegerSeriesOffset = 0x640, fixtureIntegerValuesOffset = 0x648;
BitConverter.GetBytes((uint)1).CopyTo(animatedBytes, 0x1C); BitConverter.GetBytes((uint)fixtureSequenceOffset).CopyTo(animatedBytes, 0x20);
BitConverter.GetBytes((ushort)4).CopyTo(animatedBytes, fixtureSequenceOffset); BitConverter.GetBytes((uint)1000).CopyTo(animatedBytes, fixtureSequenceOffset + 4); BitConverter.GetBytes((uint)0x20).CopyTo(animatedBytes, fixtureSequenceOffset + 12);
BitConverter.GetBytes((ushort)1).CopyTo(animatedBytes, fixtureBoneOffset + 16); BitConverter.GetBytes((short)-1).CopyTo(animatedBytes, fixtureBoneOffset + 18);
BitConverter.GetBytes((uint)1).CopyTo(animatedBytes, fixtureBoneOffset + 20); BitConverter.GetBytes((uint)fixtureTimeSeriesOffset).CopyTo(animatedBytes, fixtureBoneOffset + 24);
BitConverter.GetBytes((uint)1).CopyTo(animatedBytes, fixtureBoneOffset + 28); BitConverter.GetBytes((uint)fixtureValueSeriesOffset).CopyTo(animatedBytes, fixtureBoneOffset + 32);
BitConverter.GetBytes((uint)2).CopyTo(animatedBytes, fixtureTimeSeriesOffset); BitConverter.GetBytes((uint)fixtureTimesOffset).CopyTo(animatedBytes, fixtureTimeSeriesOffset + 4);
BitConverter.GetBytes((uint)2).CopyTo(animatedBytes, fixtureValueSeriesOffset); BitConverter.GetBytes((uint)fixtureValuesOffset).CopyTo(animatedBytes, fixtureValueSeriesOffset + 4);
BitConverter.GetBytes((uint)0).CopyTo(animatedBytes, fixtureTimesOffset); BitConverter.GetBytes((uint)1000).CopyTo(animatedBytes, fixtureTimesOffset + 4);
BitConverter.GetBytes(0f).CopyTo(animatedBytes, fixtureValuesOffset); BitConverter.GetBytes(2f).CopyTo(animatedBytes, fixtureValuesOffset + 12);
BitConverter.GetBytes((uint)2).CopyTo(animatedBytes, fixtureScalarSeriesOffset); BitConverter.GetBytes((uint)fixtureScalarValuesOffset).CopyTo(animatedBytes, fixtureScalarSeriesOffset + 4); BitConverter.GetBytes(1f).CopyTo(animatedBytes, fixtureScalarValuesOffset); BitConverter.GetBytes(1f).CopyTo(animatedBytes, fixtureScalarValuesOffset + 4);
BitConverter.GetBytes((uint)2).CopyTo(animatedBytes, fixtureRollSeriesOffset); BitConverter.GetBytes((uint)fixtureRollValuesOffset).CopyTo(animatedBytes, fixtureRollSeriesOffset + 4); BitConverter.GetBytes(0f).CopyTo(animatedBytes, fixtureRollValuesOffset); BitConverter.GetBytes(0.5f).CopyTo(animatedBytes, fixtureRollValuesOffset + 4);
BitConverter.GetBytes((uint)2).CopyTo(animatedBytes, fixtureIntegerSeriesOffset); BitConverter.GetBytes((uint)fixtureIntegerValuesOffset).CopyTo(animatedBytes, fixtureIntegerSeriesOffset + 4); BitConverter.GetBytes(1).CopyTo(animatedBytes, fixtureIntegerValuesOffset); BitConverter.GetBytes(1).CopyTo(animatedBytes, fixtureIntegerValuesOffset + 4);
BitConverter.GetBytes((uint)1).CopyTo(animatedBytes, 0x110); BitConverter.GetBytes((uint)fixtureCameraOffset).CopyTo(animatedBytes, 0x114); BitConverter.GetBytes((uint)1).CopyTo(animatedBytes, 0x118); BitConverter.GetBytes((uint)fixtureCameraLookupOffset).CopyTo(animatedBytes, 0x11C); BitConverter.GetBytes((short)0).CopyTo(animatedBytes, fixtureCameraLookupOffset);
BitConverter.GetBytes(0).CopyTo(animatedBytes, fixtureCameraOffset); BitConverter.GetBytes(MathF.PI / 4).CopyTo(animatedBytes, fixtureCameraOffset + 4); BitConverter.GetBytes(100f).CopyTo(animatedBytes, fixtureCameraOffset + 8); BitConverter.GetBytes(0.1f).CopyTo(animatedBytes, fixtureCameraOffset + 12); BitConverter.GetBytes(-5f).CopyTo(animatedBytes, fixtureCameraOffset + 40); BitConverter.GetBytes(1f).CopyTo(animatedBytes, fixtureCameraOffset + 44); BitConverter.GetBytes(1f).CopyTo(animatedBytes, fixtureCameraOffset + 76);
WriteM2Track(animatedBytes, fixtureCameraOffset + 16, fixtureTimeSeriesOffset, fixtureValueSeriesOffset); WriteM2Track(animatedBytes, fixtureCameraOffset + 48, fixtureTimeSeriesOffset, fixtureValueSeriesOffset); WriteM2Track(animatedBytes, fixtureCameraOffset + 80, fixtureTimeSeriesOffset, fixtureRollSeriesOffset);
BitConverter.GetBytes((uint)1).CopyTo(animatedBytes, 0x108); BitConverter.GetBytes((uint)fixtureLightOffset).CopyTo(animatedBytes, 0x10C); BitConverter.GetBytes((short)1).CopyTo(animatedBytes, fixtureLightOffset); BitConverter.GetBytes((short)0).CopyTo(animatedBytes, fixtureLightOffset + 2); BitConverter.GetBytes(0.5f).CopyTo(animatedBytes, fixtureLightOffset + 4);
WriteM2Track(animatedBytes, fixtureLightOffset + 16, fixtureTimeSeriesOffset, fixtureValueSeriesOffset); WriteM2Track(animatedBytes, fixtureLightOffset + 36, fixtureTimeSeriesOffset, fixtureScalarSeriesOffset); WriteM2Track(animatedBytes, fixtureLightOffset + 56, fixtureTimeSeriesOffset, fixtureValueSeriesOffset); WriteM2Track(animatedBytes, fixtureLightOffset + 76, fixtureTimeSeriesOffset, fixtureScalarSeriesOffset); WriteM2Track(animatedBytes, fixtureLightOffset + 96, fixtureTimeSeriesOffset, fixtureScalarSeriesOffset); WriteM2Track(animatedBytes, fixtureLightOffset + 116, fixtureTimeSeriesOffset, fixtureScalarSeriesOffset); WriteM2Track(animatedBytes, fixtureLightOffset + 136, fixtureTimeSeriesOffset, fixtureIntegerSeriesOffset);
for (var index = 0; index < fixtureVertices.Length; index++) { var vertex = 0x130 + index * 48; animatedBytes[vertex + 12] = 255; animatedBytes[vertex + 16] = 0; }
File.WriteAllBytes(animatedModel, animatedBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-animated00.skin"));
var animatedGeometry = M2PreviewGeometryService.Load(animatedModel); var animatedPose = M2AnimationService.CreatePose(animatedGeometry);
M2AnimationService.SampleInto(animatedGeometry, 0, 500, animatedPose);
var animatedMount = M2PreviewSceneService.AttachmentTransform(animatedGeometry.Attachments[0], animatedPose.BoneTransforms);
var animatedMountedOrigin = Vector3.Transform(Vector3.Zero, animatedMount);
if (animatedGeometry.Sequences.Count != 1 || animatedGeometry.Sequences[0].AnimationId != 4 || animatedGeometry.Sequences[0].DurationMilliseconds != 1000 ||
    Math.Abs(animatedPose.Vertices[0].X - 0f) > 0.0001f || Math.Abs(animatedPose.Vertices[1].X - 2f) > 0.0001f || Math.Abs(animatedPose.AttachmentPositions[0].X - 1.25f) > 0.0001f ||
    Math.Abs(animatedMountedOrigin.X - animatedPose.AttachmentPositions[0].X) > 0.0001f || Math.Abs(animatedMountedOrigin.Y - animatedPose.AttachmentPositions[0].Y) > 0.0001f || Math.Abs(animatedMountedOrigin.Z - animatedPose.AttachmentPositions[0].Z) > 0.0001f ||
    animatedGeometry.Cameras.Count != 1 || animatedGeometry.Cameras[0].Type != 0 || !animatedGeometry.Cameras[0].LookupSlots.SequenceEqual([0]) || animatedGeometry.Lights.Count != 1 || animatedGeometry.Lights[0].Type != 1 || animatedGeometry.Lights[0].BoneIndex != 0 ||
    Math.Abs(animatedPose.Cameras[0].Position.X - 1f) > 0.0001f || Math.Abs(animatedPose.Cameras[0].Position.Y + 5f) > 0.0001f || Math.Abs(animatedPose.Cameras[0].Target.X - 1f) > 0.0001f || Math.Abs(animatedPose.Cameras[0].RollRadians - 0.25f) > 0.0001f ||
    Math.Abs(animatedPose.Lights[0].Position.X - 1.5f) > 0.0001f || Math.Abs(animatedPose.Lights[0].AmbientColor.X - 1f) > 0.0001f || Math.Abs(animatedPose.Lights[0].DiffuseIntensity - 1f) > 0.0001f || !animatedPose.Lights[0].UseAttenuation ||
    animatedPose.SequenceIndex != 0 || Math.Abs(animatedPose.TimeMilliseconds - 500) > 0.0001)
    throw new InvalidOperationException("Wrath M2 bone/camera/light animation sampling, weighted skinning, or attachment transformation failed.");
var particleModel = Path.Combine(assetFixture, "geometry-particle.m2"); var particleBytes = animatedBytes.ToArray();
const int fixtureParticleOffset = 0x700, fixtureParticleColorsOffset = 0x680, fixtureParticleOpacityOffset = 0x6A4, fixtureParticleSizesOffset = 0x6B0;
BitConverter.GetBytes((uint)1).CopyTo(particleBytes, 0x128); BitConverter.GetBytes((uint)fixtureParticleOffset).CopyTo(particleBytes, 0x12C);
BitConverter.GetBytes(-1).CopyTo(particleBytes, fixtureParticleOffset); BitConverter.GetBytes((uint)0x00020009).CopyTo(particleBytes, fixtureParticleOffset + 4);
BitConverter.GetBytes(0.25f).CopyTo(particleBytes, fixtureParticleOffset + 8); BitConverter.GetBytes((short)0).CopyTo(particleBytes, fixtureParticleOffset + 20); BitConverter.GetBytes((ushort)0).CopyTo(particleBytes, fixtureParticleOffset + 22);
particleBytes[fixtureParticleOffset + 40] = 4; particleBytes[fixtureParticleOffset + 41] = 1; BitConverter.GetBytes((ushort)2).CopyTo(particleBytes, fixtureParticleOffset + 48); BitConverter.GetBytes((ushort)2).CopyTo(particleBytes, fixtureParticleOffset + 50);
foreach (var trackOffset in new[] { 52, 72, 92, 112, 132, 152, 176, 200, 220, 240 }) WriteM2Track(particleBytes, fixtureParticleOffset + trackOffset, fixtureTimeSeriesOffset, fixtureScalarSeriesOffset);
BitConverter.GetBytes((uint)3).CopyTo(particleBytes, fixtureParticleOffset + 268); BitConverter.GetBytes((uint)fixtureParticleColorsOffset).CopyTo(particleBytes, fixtureParticleOffset + 272);
BitConverter.GetBytes((uint)3).CopyTo(particleBytes, fixtureParticleOffset + 284); BitConverter.GetBytes((uint)fixtureParticleOpacityOffset).CopyTo(particleBytes, fixtureParticleOffset + 288);
BitConverter.GetBytes((uint)3).CopyTo(particleBytes, fixtureParticleOffset + 300); BitConverter.GetBytes((uint)fixtureParticleSizesOffset).CopyTo(particleBytes, fixtureParticleOffset + 304);
for (var key = 0; key < 3; key++) { BitConverter.GetBytes(key == 1 ? 128f : 255f).CopyTo(particleBytes, fixtureParticleColorsOffset + key * 12); BitConverter.GetBytes(64f).CopyTo(particleBytes, fixtureParticleColorsOffset + key * 12 + 4); BitConverter.GetBytes(32f).CopyTo(particleBytes, fixtureParticleColorsOffset + key * 12 + 8); BitConverter.GetBytes((short)(key == 2 ? 0 : 32767)).CopyTo(particleBytes, fixtureParticleOpacityOffset + key * 2); BitConverter.GetBytes(0.25f + key * 0.125f).CopyTo(particleBytes, fixtureParticleSizesOffset + key * 8); BitConverter.GetBytes(0.25f + key * 0.125f).CopyTo(particleBytes, fixtureParticleSizesOffset + key * 8 + 4); }
BitConverter.GetBytes(1f).CopyTo(particleBytes, fixtureParticleOffset + 360); BitConverter.GetBytes(1f).CopyTo(particleBytes, fixtureParticleOffset + 364); BitConverter.GetBytes(1f).CopyTo(particleBytes, fixtureParticleOffset + 368);
File.WriteAllBytes(particleModel, particleBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-particle00.skin"));
var particleGeometry = M2PreviewGeometryService.Load(particleModel); var particlePose = M2AnimationService.CreatePose(particleGeometry); M2AnimationService.SampleInto(particleGeometry, 0, 500, particlePose);
var particleSprites = M2ParticlePreviewService.BuildSprites(particleGeometry, particlePose, 32);
if (particleGeometry.ParticleEmitters.Count != 1 || particleGeometry.ParticleEmitters[0].EmitterType != 1 || particleGeometry.ParticleEmitters[0].TextureDefinitionIndex != 0 || particleGeometry.ParticleEmitters[0].Rows != 2 || particleGeometry.ParticleEmitters[0].Columns != 2 || !particleGeometry.UsedTextureDefinitionIndices.SequenceEqual([0]) ||
    particleSprites.Count != 1 || particleSprites[0].TileIndex is < 0 or > 3 || particleSprites[0].Color.W <= 0 || !float.IsFinite(particleSprites[0].Position.X) || particleSprites[0].Size <= 0)
    throw new InvalidOperationException("Wrath M2 particle parsing, animation sampling, deterministic emission, life ramp, or sprite-sheet selection failed.");
var multiParticleModel = Path.Combine(assetFixture, "geometry-particle-multi.m2"); var multiParticleBytes = new byte[0xC00]; particleBytes.CopyTo(multiParticleBytes, 0);
const int multiTextureDefinitionsOffset = 0xA00, multiTextureNamesOffset = 0xA30;
BitConverter.GetBytes((uint)3).CopyTo(multiParticleBytes, 0x50); BitConverter.GetBytes((uint)multiTextureDefinitionsOffset).CopyTo(multiParticleBytes, 0x54);
var multiTextureNames = new[] { "Particles\\Base.blp\0", "Particles\\Mask.blp\0", "Particles\\Detail.blp\0" }; var multiNameOffset = multiTextureNamesOffset;
for (var index = 0; index < multiTextureNames.Length; index++) { var bytes = System.Text.Encoding.ASCII.GetBytes(multiTextureNames[index]); BitConverter.GetBytes((uint)bytes.Length).CopyTo(multiParticleBytes, multiTextureDefinitionsOffset + index * 16 + 8); BitConverter.GetBytes((uint)multiNameOffset).CopyTo(multiParticleBytes, multiTextureDefinitionsOffset + index * 16 + 12); bytes.CopyTo(multiParticleBytes, multiNameOffset); multiNameOffset += bytes.Length; }
BitConverter.GetBytes((uint)0x10020009).CopyTo(multiParticleBytes, fixtureParticleOffset + 4); BitConverter.GetBytes((ushort)(0 | (1 << 5) | (2 << 10))).CopyTo(multiParticleBytes, fixtureParticleOffset + 22);
File.WriteAllBytes(multiParticleModel, multiParticleBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-particle-multi00.skin"));
var multiParticleGeometry = M2PreviewGeometryService.Load(multiParticleModel); var multiParticlePose = M2AnimationService.CreatePose(multiParticleGeometry); M2AnimationService.SampleInto(multiParticleGeometry, 0, 500, multiParticlePose); var multiParticleSprites = M2ParticlePreviewService.BuildSprites(multiParticleGeometry, multiParticlePose, 32);
var composedParticleTexture = M2ParticleTextureCompositionService.Compose([new(1, 1, [100, 10, 255, 64]), new(1, 1, [128, 255, 64, 128]), new(1, 1, [255, 128, 255, 255])]);
if (!multiParticleGeometry.ParticleEmitters.Single().TextureDefinitionIndices.SequenceEqual([0, 1, 2]) || !multiParticleGeometry.UsedTextureDefinitionIndices.SequenceEqual([0, 1, 2]) || !multiParticleSprites.Single().TextureDefinitionIndices.SequenceEqual([0, 1, 2]) || !composedParticleTexture.Pixels.SequenceEqual(new byte[] { 128, 20, 64, 128 }))
    throw new InvalidOperationException("WotLK packed particle texture indices, dependency exposure, sprite propagation, or fixed-function modulation failed.");
try { _ = M2ParticleTextureCompositionService.Compose([new(1, 1, [1, 2, 3])]); throw new InvalidOperationException("Malformed RGBA input was accepted for particle composition."); } catch (InvalidDataException) { }
var invalidMultiParticleModel = Path.Combine(assetFixture, "geometry-invalid-particle-multi.m2"); var invalidMultiParticleBytes = multiParticleBytes.ToArray(); BitConverter.GetBytes((ushort)(0 | (1 << 5) | (3 << 10))).CopyTo(invalidMultiParticleBytes, fixtureParticleOffset + 22); File.WriteAllBytes(invalidMultiParticleModel, invalidMultiParticleBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-invalid-particle-multi00.skin"));
try { _ = M2PreviewGeometryService.Load(invalidMultiParticleModel); throw new InvalidOperationException("A packed particle texture index beyond the texture table was accepted."); } catch (InvalidDataException exception) when (exception.Message.Contains("packed multi-texture", StringComparison.Ordinal)) { }
var ribbonModel = Path.Combine(assetFixture, "geometry-ribbon.m2"); var ribbonBytes = new byte[0xC00]; animatedBytes.CopyTo(ribbonBytes, 0);
const int fixtureRibbonOffset = 0x700, fixtureRibbonTextureOffset = 0x800, fixtureRibbonMaterialOffset = 0x804, fixtureRibbonOpacitySeriesOffset = 0x810, fixtureRibbonOpacityValuesOffset = 0x820;
BitConverter.GetBytes((uint)1).CopyTo(ribbonBytes, 0x120); BitConverter.GetBytes((uint)fixtureRibbonOffset).CopyTo(ribbonBytes, 0x124);
BitConverter.GetBytes(-1).CopyTo(ribbonBytes, fixtureRibbonOffset); BitConverter.GetBytes(0).CopyTo(ribbonBytes, fixtureRibbonOffset + 4); BitConverter.GetBytes(0.25f).CopyTo(ribbonBytes, fixtureRibbonOffset + 8);
BitConverter.GetBytes((uint)1).CopyTo(ribbonBytes, fixtureRibbonOffset + 20); BitConverter.GetBytes((uint)fixtureRibbonTextureOffset).CopyTo(ribbonBytes, fixtureRibbonOffset + 24); BitConverter.GetBytes(0).CopyTo(ribbonBytes, fixtureRibbonTextureOffset);
BitConverter.GetBytes((uint)1).CopyTo(ribbonBytes, fixtureRibbonOffset + 28); BitConverter.GetBytes((uint)fixtureRibbonMaterialOffset).CopyTo(ribbonBytes, fixtureRibbonOffset + 32); BitConverter.GetBytes(0).CopyTo(ribbonBytes, fixtureRibbonMaterialOffset);
WriteM2Track(ribbonBytes, fixtureRibbonOffset + 36, fixtureTimeSeriesOffset, fixtureValueSeriesOffset); WriteM2Track(ribbonBytes, fixtureRibbonOffset + 56, fixtureTimeSeriesOffset, fixtureRibbonOpacitySeriesOffset); WriteM2Track(ribbonBytes, fixtureRibbonOffset + 76, fixtureTimeSeriesOffset, fixtureScalarSeriesOffset); WriteM2Track(ribbonBytes, fixtureRibbonOffset + 96, fixtureTimeSeriesOffset, fixtureScalarSeriesOffset);
BitConverter.GetBytes((uint)2).CopyTo(ribbonBytes, fixtureRibbonOpacitySeriesOffset); BitConverter.GetBytes((uint)fixtureRibbonOpacityValuesOffset).CopyTo(ribbonBytes, fixtureRibbonOpacitySeriesOffset + 4); BitConverter.GetBytes((ushort)32767).CopyTo(ribbonBytes, fixtureRibbonOpacityValuesOffset); BitConverter.GetBytes((ushort)16384).CopyTo(ribbonBytes, fixtureRibbonOpacityValuesOffset + 2);
BitConverter.GetBytes(10f).CopyTo(ribbonBytes, fixtureRibbonOffset + 116); BitConverter.GetBytes(0.5f).CopyTo(ribbonBytes, fixtureRibbonOffset + 120);
File.WriteAllBytes(ribbonModel, ribbonBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-ribbon00.skin"));
var ribbonGeometry = M2PreviewGeometryService.Load(ribbonModel); var ribbonPose = M2AnimationService.CreatePose(ribbonGeometry); M2AnimationService.SampleInto(ribbonGeometry, 0, 500, ribbonPose);
var ribbonTrails = M2RibbonPreviewService.BuildTrails(ribbonGeometry, ribbonPose, 32);
if (ribbonGeometry.RibbonEmitters.Count != 1 || ribbonGeometry.RibbonEmitters[0].TextureDefinitionIndex != 0 || ribbonGeometry.RibbonEmitters[0].RenderFlagsIndex != 0 || ribbonGeometry.RibbonEmitters[0].BlendMode != 2 ||
    !ribbonGeometry.UsedTextureDefinitionIndices.Contains(0) || ribbonTrails.Count != 1 || ribbonTrails[0].Sections.Count != 6 || ribbonTrails[0].Color.W is < 0.74f or > 0.76f ||
    Vector3.Distance(ribbonTrails[0].Sections[0].Center, ribbonTrails[0].Sections[^1].Center) <= 0.1f)
    throw new InvalidOperationException($"Wrath M2 ribbon parsing, material binding, past-bone sampling, width/color animation, or bounded trail generation failed: emitters={ribbonGeometry.RibbonEmitters.Count}, texture={(ribbonGeometry.RibbonEmitters.Count == 0 ? -9 : ribbonGeometry.RibbonEmitters[0].TextureDefinitionIndex)}, flags={(ribbonGeometry.RibbonEmitters.Count == 0 ? -9 : ribbonGeometry.RibbonEmitters[0].RenderFlagsIndex)}, blend={(ribbonGeometry.RibbonEmitters.Count == 0 ? -9 : ribbonGeometry.RibbonEmitters[0].BlendMode)}, used={string.Join(',', ribbonGeometry.UsedTextureDefinitionIndices)}, trails={ribbonTrails.Count}, sections={(ribbonTrails.Count == 0 ? 0 : ribbonTrails[0].Sections.Count)}, alpha={(ribbonTrails.Count == 0 ? -1 : ribbonTrails[0].Color.W)}, distance={(ribbonTrails.Count == 0 ? -1 : Vector3.Distance(ribbonTrails[0].Sections[0].Center, ribbonTrails[0].Sections[^1].Center))}.");
var nativeCamera = animatedGeometry.Cameras[0]; var projection = M2CameraProjectionService.TryCreate(nativeCamera, new(nativeCamera.BasePosition, nativeCamera.BaseTarget, 0), Matrix4x4.Identity) ?? throw new InvalidOperationException("A valid native M2 camera did not produce a projection basis.");
var projectedTarget = projection.Project(projection.ToViewPoint(nativeCamera.BaseTarget)); var projectedRight = projection.Project(projection.ToViewPoint(nativeCamera.BaseTarget + Vector3.UnitX));
if (nativeCamera.FieldOfViewDegrees is < 27f or > 28f || Math.Abs(projectedTarget.X) > 0.0001f || Math.Abs(projectedTarget.Z) > 0.0001f || projectedRight.X <= 0 || !projection.ContainsDepth(5f) || projection.ContainsDepth(nativeCamera.NearClip) ||
    M2CameraProjectionService.TryCreate(nativeCamera, new(nativeCamera.BasePosition, nativeCamera.BasePosition, 0), Matrix4x4.Identity) is not null)
    throw new InvalidOperationException("Native M2 perspective camera basis, FOV conversion, clipping, or degeneracy checks regressed.");
var modelExportPath = Path.Combine(assetFixture, "exports", "animated-visible.obj");
var modelExport = M2ObjExportService.Export(animatedGeometry, modelExportPath, animatedPose, new Dictionary<int, RgbaTexture> { [0] = new(1, 1, [12, 34, 56, 255]) });
var exportedObj = File.ReadAllText(modelExport.ObjPath); var exportedMtl = File.ReadAllText(modelExport.MaterialPath);
var exportReceipt = JsonSerializer.Deserialize<M2ObjExportReceipt>(File.ReadAllText(modelExport.ReceiptPath)) ?? throw new InvalidDataException("M2 OBJ export receipt is empty.");
if (!modelExport.Posed || modelExport.Vertices != 3 || modelExport.Triangles != 2 || modelExport.TexturePaths.Count != 1 ||
    exportedObj.Split('\n').Count(line => line.StartsWith("v ", StringComparison.Ordinal)) != 3 || exportedObj.Split('\n').Count(line => line.StartsWith("f ", StringComparison.Ordinal)) != 2 ||
    !exportedObj.Contains("# animation sequence=0 time-ms=500", StringComparison.Ordinal) || !exportedObj.Contains("v 0 0 -1", StringComparison.Ordinal) ||
    !exportedMtl.Contains("map_Kd animated-visible-texture-000.png", StringComparison.Ordinal) || !File.Exists(modelExport.TexturePaths[0]) ||
    exportReceipt.Format != "wow-crucible-m2-obj-v2" || exportReceipt.AnimationSequenceIndex != 0 || exportReceipt.AnimationTimeMilliseconds != 500 ||
    exportReceipt.Materials.Count != 2 || exportReceipt.Materials[0].Combiner != "Opaque_Opaque" || exportReceipt.Materials[0].TextureStages.Count != 2 || exportReceipt.Materials[0].TextureStages[1].CoordinateSource != M2PreviewTextureCoordinateSource.Secondary || exportReceipt.Triangles != 2 || exportReceipt.TextureFiles.Single() != "animated-visible-texture-000.png")
    throw new InvalidOperationException("Native M2 OBJ export did not preserve visible geosets, sampled pose, materials, texture output, or its provenance receipt.");
try { _ = M2ObjExportService.Export(animatedGeometry, modelExportPath, animatedPose); throw new InvalidOperationException("M2 OBJ export silently replaced an existing output without --overwrite."); }
catch (IOException) { }
var invalidAttachmentModel = Path.Combine(assetFixture, "geometry-invalid.m2"); var invalidAttachmentBytes = geometryBytes.ToArray(); BitConverter.GetBytes((uint)2).CopyTo(invalidAttachmentBytes, fixtureAttachmentOffset + 4); File.WriteAllBytes(invalidAttachmentModel, invalidAttachmentBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-invalid00.skin"));
try { _ = M2PreviewGeometryService.Load(invalidAttachmentModel); throw new InvalidOperationException("An M2 attachment pointing beyond the bone table was accepted."); }
catch (InvalidDataException exception) when (exception.Message.Contains("references bone", StringComparison.Ordinal)) { }
var invalidCameraModel = Path.Combine(assetFixture, "geometry-invalid-camera.m2"); var invalidCameraBytes = animatedBytes.ToArray(); BitConverter.GetBytes((short)2).CopyTo(invalidCameraBytes, fixtureCameraLookupOffset); File.WriteAllBytes(invalidCameraModel, invalidCameraBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-invalid-camera00.skin"));
try { _ = M2PreviewGeometryService.Load(invalidCameraModel); throw new InvalidOperationException("An M2 camera lookup pointing beyond the camera table was accepted."); }
catch (InvalidDataException exception) when (exception.Message.Contains("camera lookup", StringComparison.Ordinal)) { }
var invalidLightModel = Path.Combine(assetFixture, "geometry-invalid-light.m2"); var invalidLightBytes = animatedBytes.ToArray(); BitConverter.GetBytes((short)2).CopyTo(invalidLightBytes, fixtureLightOffset + 2); File.WriteAllBytes(invalidLightModel, invalidLightBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-invalid-light00.skin"));
try { _ = M2PreviewGeometryService.Load(invalidLightModel); throw new InvalidOperationException("An M2 light pointing beyond the bone table was accepted."); }
catch (InvalidDataException exception) when (exception.Message.Contains("light", StringComparison.Ordinal) && exception.Message.Contains("references bone", StringComparison.Ordinal)) { }
var invalidParticleModel = Path.Combine(assetFixture, "geometry-invalid-particle.m2"); var invalidParticleBytes = particleBytes.ToArray(); BitConverter.GetBytes((short)2).CopyTo(invalidParticleBytes, fixtureParticleOffset + 20); File.WriteAllBytes(invalidParticleModel, invalidParticleBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-invalid-particle00.skin"));
try { _ = M2PreviewGeometryService.Load(invalidParticleModel); throw new InvalidOperationException("An M2 particle emitter pointing beyond the bone table was accepted."); }
catch (InvalidDataException exception) when (exception.Message.Contains("particle emitter", StringComparison.Ordinal) && exception.Message.Contains("references bone", StringComparison.Ordinal)) { }
var invalidRibbonModel = Path.Combine(assetFixture, "geometry-invalid-ribbon.m2"); var invalidRibbonBytes = ribbonBytes.ToArray(); BitConverter.GetBytes(2).CopyTo(invalidRibbonBytes, fixtureRibbonMaterialOffset); File.WriteAllBytes(invalidRibbonModel, invalidRibbonBytes); File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(assetFixture, "geometry-invalid-ribbon00.skin"));
try { _ = M2PreviewGeometryService.Load(invalidRibbonModel); throw new InvalidOperationException("An M2 ribbon emitter pointing beyond the render-flags table was accepted."); }
catch (InvalidDataException exception) when (exception.Message.Contains("ribbon emitter", StringComparison.Ordinal) && exception.Message.Contains("render flags", StringComparison.Ordinal)) { }
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
var previewWmo = Path.Combine(assetFixture, "preview-building.wmo"); var previewWmoGroup = Path.Combine(assetFixture, "preview-building_000.wmo");
var previewMohd = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(previewMohd.AsSpan(4, 4), 1);
var previewMotx = System.Text.Encoding.UTF8.GetBytes("Textures\\Fixture\\Stone.blp\0"); var previewMomt = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(previewMomt.AsSpan(12, 4), 0);
File.WriteAllBytes(previewWmo, GraphChunks(("MVER", BitConverter.GetBytes((uint)17)), ("MOHD", previewMohd), ("MOTX", previewMotx), ("MOMT", previewMomt)));
var previewMopy = new byte[] { 0x20, 0 }; var previewMovi = new byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(previewMovi.AsSpan(0, 2), 0); BinaryPrimitives.WriteUInt16LittleEndian(previewMovi.AsSpan(2, 2), 1); BinaryPrimitives.WriteUInt16LittleEndian(previewMovi.AsSpan(4, 2), 2);
var previewMovt = new byte[36]; var previewMonr = new byte[36]; var previewMotv = new byte[24];
var wmoFixtureVertices = new[] { new Vector3(0,0,0), new Vector3(2,0,0), new Vector3(0,0,3) }; var wmoFixtureUvs = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };
for (var index = 0; index < 3; index++)
{
    BinaryPrimitives.WriteSingleLittleEndian(previewMovt.AsSpan(index * 12, 4), wmoFixtureVertices[index].X); BinaryPrimitives.WriteSingleLittleEndian(previewMovt.AsSpan(index * 12 + 4, 4), wmoFixtureVertices[index].Y); BinaryPrimitives.WriteSingleLittleEndian(previewMovt.AsSpan(index * 12 + 8, 4), wmoFixtureVertices[index].Z);
    BinaryPrimitives.WriteSingleLittleEndian(previewMonr.AsSpan(index * 12 + 4, 4), 1); BinaryPrimitives.WriteSingleLittleEndian(previewMotv.AsSpan(index * 8, 4), wmoFixtureUvs[index].X); BinaryPrimitives.WriteSingleLittleEndian(previewMotv.AsSpan(index * 8 + 4, 4), wmoFixtureUvs[index].Y);
}
var previewMogp = new byte[68 + GraphChunks(("MOPY", previewMopy), ("MOVI", previewMovi), ("MOVT", previewMovt), ("MONR", previewMonr), ("MOTV", previewMotv)).Length];
GraphChunks(("MOPY", previewMopy), ("MOVI", previewMovi), ("MOVT", previewMovt), ("MONR", previewMonr), ("MOTV", previewMotv)).CopyTo(previewMogp, 68);
File.WriteAllBytes(previewWmoGroup, GraphChunks(("MVER", BitConverter.GetBytes((uint)17)), ("MOGP", previewMogp)));
var wmoGeometry = WmoPreviewGeometryService.Load(previewWmo); var groupSelectedGeometry = WmoPreviewGeometryService.Load(previewWmoGroup);
if (wmoGeometry.Version != 17 || wmoGeometry.Groups.Count != 1 || wmoGeometry.Vertices.Count != 3 || wmoGeometry.TriangleIndices.Count != 3 || wmoGeometry.Batches.Count != 1 ||
    wmoGeometry.Materials.Single().Texture1 != "Textures\\Fixture\\Stone.blp" || wmoGeometry.Minimum != Vector3.Zero || wmoGeometry.Maximum != new Vector3(2,0,3) || groupSelectedGeometry.RootPath != Path.GetFullPath(previewWmo))
    throw new InvalidOperationException("Native WMO preview did not decode root materials, group geometry, bounds, or group-to-root selection.");
var wmoWorkspacePath = Path.Combine(Path.GetTempPath(), $"crucible-wmo-workspace-{Guid.NewGuid():N}"); var wmoWorkspace = NativeAssetConversionService.CreateWorkspace([previewWmo], wmoWorkspacePath); var wmoAsset = wmoWorkspace.Assets.Single(); var wmoDependency = wmoAsset.Dependencies.Single();
var snapshotWmoGeometry = WmoPreviewGeometryService.Load(NativeAssetConversionService.ResolveSnapshotPath(wmoWorkspace, wmoAsset), new Dictionary<int, string> { [0] = NativeAssetConversionService.ResolveDependencySnapshotPath(wmoWorkspace, wmoAsset, wmoDependency) });
if (snapshotWmoGeometry.TriangleIndices.Count != 3) throw new InvalidOperationException("Portable native-conversion WMO snapshots could not resolve namespaced group geometry."); Directory.Delete(wmoWorkspacePath, true);
var corruptGroup = File.ReadAllBytes(previewWmoGroup); var moviSignature = System.Text.Encoding.ASCII.GetBytes("IVOM"); var moviOffset = corruptGroup.AsSpan().IndexOf(moviSignature); BinaryPrimitives.WriteUInt16LittleEndian(corruptGroup.AsSpan(moviOffset + 8, 2), 9); File.WriteAllBytes(Path.Combine(assetFixture, "preview-building_001.wmo"), corruptGroup);
BinaryPrimitives.WriteUInt32LittleEndian(previewMohd.AsSpan(4, 4), 2); File.WriteAllBytes(previewWmo, GraphChunks(("MVER", BitConverter.GetBytes((uint)17)), ("MOHD", previewMohd), ("MOTX", previewMotx), ("MOMT", previewMomt)));
var partialWmoGeometry = WmoPreviewGeometryService.Load(previewWmo);
if (partialWmoGeometry.Groups.Count != 1 || partialWmoGeometry.Vertices.Count != 3 || partialWmoGeometry.TriangleIndices.Count != 3 || !partialWmoGeometry.Findings.Any(value => value.Contains("beyond", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("A rejected WMO group contaminated the geometry retained from an earlier valid group.");
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
File.Copy(Path.Combine(assetFixture, "geometry00.skin"), Path.Combine(comparisonRoot, "Archives", "Content", "Character", "BloodElf", "Female", "patch-old-a1", "geometry_lod00.skin"));
var comparisonIndex = AssetComparisonService.BuildIndex(comparisonRoot); var comparisonEntries = AssetComparisonService.GetDirectoryPngs(comparisonIndex, @"Character\BloodElf\Female");
var matchingDirectory = comparisonIndex.Directories.Single(directory => directory.LogicalPath == @"Character\BloodElf\Female");
var looseOnlyEntries = AssetComparisonService.GetDirectoryPngs(comparisonIndex, @"Character\Human\Female");
var exactDuplicates = AssetComparisonService.FindExactDuplicates(comparisonEntries);
var comparisonModels = AssetComparisonService.GetDirectoryModels(comparisonIndex, @"Character\BloodElf\Female");
var ancestorModels = AssetComparisonService.GetRelevantModels(comparisonIndex, @"Character\BloodElf\Female\Hair");
var unrelatedModels = AssetComparisonService.GetRelevantModels(comparisonIndex, @"Character\Human\Female\Hair");
if (comparisonIndex.TotalPngFiles != 9 || matchingDirectory.ProvenanceSources != 3 || comparisonEntries.Count != 4 || comparisonEntries.Select(entry => entry.FileName).Distinct().Count() != 4 ||
    looseOnlyEntries.Count != 1 || looseOnlyEntries[0].Provenance != "ExtendedSkins" || exactDuplicates.Count != 1 || exactDuplicates[0].Entries.Count != 2 || exactDuplicates[0].RecoverableBytes != 3 || comparisonModels.Count != 1 || comparisonModels[0].Compatibility != AssetModelCompatibility.Ready || comparisonModels[0].SkinPaths.Count != 2 || Path.GetFileName(comparisonModels[0].SkinPath) != "geometry00.skin" || ancestorModels.DiscoveryScope != @"Character\BloodElf\Female" || ancestorModels.Models.Count != 1 || unrelatedModels.Models.Count != 0)
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
var graphMmid=new byte[4];var graphMddf=new byte[36];BinaryPrimitives.WriteUInt32LittleEndian(graphMddf.AsSpan(4,4),654321);var graphM2Position=new Vector3(7,8,9);var graphM2Orientation=new Vector3(15,25,35);var graphM2Vectors=new[]{graphM2Position,graphM2Orientation};for(var vectorIndex=0;vectorIndex<graphM2Vectors.Length;vectorIndex++){var vector=graphM2Vectors[vectorIndex];var offset=8+vectorIndex*12;BinaryPrimitives.WriteSingleLittleEndian(graphMddf.AsSpan(offset,4),vector.X);BinaryPrimitives.WriteSingleLittleEndian(graphMddf.AsSpan(offset+4,4),vector.Y);BinaryPrimitives.WriteSingleLittleEndian(graphMddf.AsSpan(offset+8,4),vector.Z);}BinaryPrimitives.WriteUInt16LittleEndian(graphMddf.AsSpan(32,2),768);BinaryPrimitives.WriteUInt16LittleEndian(graphMddf.AsSpan(34,2),1);
var graphMwid=new byte[4];var graphModf=new byte[64];BinaryPrimitives.WriteUInt32LittleEndian(graphModf.AsSpan(4,4),123456);var graphPlacementPosition=new Vector3(1,2,3);var graphPlacementOrientation=new Vector3(10,20,30);var graphPlacementMinimum=new Vector3(-4,-5,-6);var graphPlacementMaximum=new Vector3(4,5,6);
var graphPlacementVectors=new[]{graphPlacementPosition,graphPlacementOrientation,graphPlacementMinimum,graphPlacementMaximum};for(var vectorIndex=0;vectorIndex<graphPlacementVectors.Length;vectorIndex++){var vector=graphPlacementVectors[vectorIndex];var offset=8+vectorIndex*12;BinaryPrimitives.WriteSingleLittleEndian(graphModf.AsSpan(offset,4),vector.X);BinaryPrimitives.WriteSingleLittleEndian(graphModf.AsSpan(offset+4,4),vector.Y);BinaryPrimitives.WriteSingleLittleEndian(graphModf.AsSpan(offset+8,4),vector.Z);}BinaryPrimitives.WriteUInt16LittleEndian(graphModf.AsSpan(56,2),4);BinaryPrimitives.WriteUInt16LittleEndian(graphModf.AsSpan(58,2),2);BinaryPrimitives.WriteUInt16LittleEndian(graphModf.AsSpan(60,2),3);BinaryPrimitives.WriteUInt16LittleEndian(graphModf.AsSpan(62,2),512);
var graphAdt=WriteGraphAsset(dependencyContent,dependencySource,@"World\Maps\Graph\Graph_1_1.adt",GraphChunks(("MVER",BitConverter.GetBytes((uint)18)),("MTEX",GraphStrings(@"Textures\Graph\Terrain.blp")),("MMDX",GraphStrings(@"World\Model\Graph\Doodad.m2")),("MMID",graphMmid),("MDDF",graphMddf),("MWMO",GraphStrings(@"World\Wmo\Graph\House.wmo")),("MWID",graphMwid),("MODF",graphModf)));
var recursiveIndex=AssetComparisonService.BuildIndex(comparisonRoot);var recursiveGraph=ClientAssetDependencyService.Analyze(recursiveIndex,graphAdt);
if(recursiveGraph.Blocking.Count!=0||recursiveGraph.PatchEntries.Count!=8||!recursiveGraph.Nodes.Any(node=>node.Kind=="wmo-group")||!recursiveGraph.Nodes.Any(node=>node.Kind=="wmo-doodad-model")||!recursiveGraph.Nodes.Any(node=>node.Kind=="embedded-texture")||!recursiveGraph.Nodes.Any(node=>node.Kind=="terrain-texture"))
    throw new InvalidOperationException($"Recursive ADT/WMO/M2/SKIN/BLP dependency closure was incomplete: files={recursiveGraph.PatchEntries.Count}, blocking={recursiveGraph.Blocking.Count}.");
var graphMapInspection=MapAssetInspectionService.Inspect(graphAdt);var graphPlacement=graphMapInspection.WmoPlacements.Single();if(graphPlacement.UniqueId!=123456||graphPlacement.ClientPath!=@"World\Wmo\Graph\House.wmo"||graphPlacement.Position!=graphPlacementPosition||graphPlacement.Orientation!=graphPlacementOrientation||graphPlacement.MinimumExtent!=graphPlacementMinimum||graphPlacement.MaximumExtent!=graphPlacementMaximum||graphPlacement.Flags!=4||graphPlacement.DoodadSet!=2||graphPlacement.NameSet!=3||graphPlacement.ScaleRaw!=512||graphPlacement.Scale!=0.5f)throw new InvalidOperationException("ADT MODF placement decoding lost a WMO name, transform, extents, flags, sets, or scale.");
var graphM2Placement=graphMapInspection.M2Placements.Single();if(graphM2Placement.UniqueId!=654321||graphM2Placement.ClientPath!=@"World\Model\Graph\Doodad.m2"||graphM2Placement.Position!=graphM2Position||graphM2Placement.Orientation!=graphM2Orientation||graphM2Placement.ScaleRaw!=768||graphM2Placement.Scale!=0.75f||graphM2Placement.Flags!=1)throw new InvalidOperationException("ADT MDDF placement decoding lost an M2 name, transform, scale, or flags.");
var graphFallbackAdt=Path.Combine(assetFixture,"GraphFallback_1_1.adt");File.WriteAllBytes(graphFallbackAdt,GraphChunks(("MVER",BitConverter.GetBytes((uint)18)),("MWMO",GraphStrings(@"World\Wmo\Graph\House.wmo")),("MODF",graphModf)));if(MapAssetInspectionService.Inspect(graphFallbackAdt).WmoPlacements.Single().ClientPath!=@"World\Wmo\Graph\House.wmo")throw new InvalidOperationException("Single-name global WMO placement did not resolve without an MWID table.");
var mapWmoCandidates=ClientAssetDependencyService.FindCandidates(recursiveIndex,@"World\Wmo\Graph\House.wmo");var mapLocation=ClientAssetDependencyService.InferLocation(recursiveIndex,graphAdt);
if(mapWmoCandidates.Count!=1||mapWmoCandidates[0].Provenance!=dependencySource||mapWmoCandidates[0].SourcePath!=Path.GetFullPath(Path.Combine(dependencyContent,"World","Wmo","Graph",dependencySource,"House.wmo"))||mapLocation.Provenance!=dependencySource)
    throw new InvalidOperationException("Processed-library client-path candidate resolution lost the map/WMO provenance relationship.");
var alternateMapWmo=WriteGraphAsset(dependencyContent,"alternate-source",@"World\Wmo\Graph\House.wmo",GraphChunks(("MVER",BitConverter.GetBytes((uint)17)),("MOHD",graphMohd)));
if(ClientAssetDependencyService.FindCandidates(recursiveIndex,@"World\Wmo\Graph\House.wmo").Count!=2)throw new InvalidOperationException("Processed-library path resolution hid an ambiguous provenance candidate.");File.Delete(alternateMapWmo);
var graphManifest=Path.Combine(assetFixture,"graph.crucible-patch.json");PatchManifestService.Save(graphManifest,"graph fixture","patch-graph.MPQ",recursiveGraph.PatchEntries,policy:new(ExpectedEntryCount:recursiveGraph.PatchEntries.Count));if(!PatchManifestService.Validate(PatchManifestService.Load(graphManifest)).Passed)throw new InvalidOperationException("Dependency closure did not produce a valid portable patch manifest.");
var otherTerrain=Path.Combine(dependencyContent,"Textures","Graph","other-source","Terrain.blp");Directory.CreateDirectory(Path.GetDirectoryName(otherTerrain)!);File.Move(graphTerrain,otherTerrain);var conflictingGraph=ClientAssetDependencyService.Analyze(recursiveIndex,graphAdt);var explicitGraph=ClientAssetDependencyService.Analyze(recursiveIndex,ClientAssetDependencyService.InferLocation(recursiveIndex,graphAdt),new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){[@"Textures\Graph\Terrain.blp"]=otherTerrain});File.Move(otherTerrain,graphTerrain);
if(!conflictingGraph.Blocking.Any(node=>node.State==ClientAssetDependencyState.CrossSourceConflict&&node.ClientPath.Equals(@"Textures\Graph\Terrain.blp",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Recursive dependency closure silently mixed a required asset from another provenance layer.");
if(explicitGraph.Blocking.Count!=0||!explicitGraph.Nodes.Any(node=>node.ClientPath.Equals(@"Textures\Graph\Terrain.blp",StringComparison.OrdinalIgnoreCase)&&node.Provenance=="other-source"&&node.State==ClientAssetDependencyState.Resolved))throw new InvalidOperationException("An explicit cross-provenance dependency resolution did not produce a complete auditable graph.");
definitive = DefinitiveAssetProjectService.RecordModel(definitivePath, definitive, comparisonIndex, comparisonModels[0], AssetDecision.Keeper, "Race", "fixture model");
var definitiveStage = DefinitiveAssetProjectService.StageKeepers(definitivePath, definitive, Path.Combine(assetFixture, "definitive-stage"));
if (definitive.Entries.Count != 5 || definitiveStage.Files != 5 || !File.Exists(definitiveStage.ManifestPath) || !PatchManifestService.Validate(PatchManifestService.Load(definitiveStage.ManifestPath)).Passed)
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
var mountIds = CrucibleContentProjectService.ReserveIds(contentProjectRoot, ContentIdDomain.Mount, 1, 100_000, [], "fixture mount").Reservation.Values;
var spellIdsAfterMount = CrucibleContentProjectService.ReserveIds(contentProjectRoot, ContentIdDomain.Spell, 1, 100_000, [], "fixture spell").Reservation.Values;
var raceOccupancy = await new ContentIdOccupancyService().InspectAsync(ContentIdDomain.Race, null, null, args[1], args[0]);
var customOccupancy = await new ContentIdOccupancyService().InspectAsync(ContentIdDomain.Custom, null, null, null, null, [7u, 9u]);
var incompleteItemOccupancy = await new ContentIdOccupancyService().InspectAsync(ContentIdDomain.Item, null, null, null, null);
var reservedRace = CrucibleContentProjectService.ReserveVerifiedIds(contentProjectRoot, raceOccupancy, 1, null, "fixture race").Reservation.Values.Single();
static ContentIdOccupancyReport CompleteSqlOccupancy(ContentIdDomain domain, string table) => new(domain, ContentIdDomainCatalog.RegistryNamespace(domain), DateTimeOffset.UnixEpoch, [100_000u], [new("SQL", table, "fixture", 1, true, "fixture")], true, []);
var reservedCreature = CrucibleContentProjectService.ReserveVerifiedIds(contentProjectRoot, CompleteSqlOccupancy(ContentIdDomain.CreatureTemplate, "creature_template.entry"), 1, null, "fixture creature").Reservation.Values.Single();
var reservedGameObject = CrucibleContentProjectService.ReserveVerifiedIds(contentProjectRoot, CompleteSqlOccupancy(ContentIdDomain.GameObject, "gameobject_template.entry"), 1, null, "fixture gameobject").Reservation.Values.Single();
var reservedQuest = CrucibleContentProjectService.ReserveVerifiedIds(contentProjectRoot, CompleteSqlOccupancy(ContentIdDomain.Quest, "quest_template.ID"), 1, null, "fixture quest").Reservation.Values.Single();
if (defaultContentProject.TargetProfile != TargetProfileCatalog.DefaultProfileId || TargetProfileCatalog.Find(targetProfiles, defaultContentProject.TargetProfile).Id != TargetProfileCatalog.DefaultProfileId ||
    !firstIds.SequenceEqual([101u, 103u, 104u]) || !secondIds.SequenceEqual([105u, 106u]) || !mountIds.SequenceEqual([100_000u]) || !spellIdsAfterMount.SequenceEqual([100_001u]) ||
    !raceOccupancy.Complete || raceOccupancy.RegistryNamespace != ContentIdDomain.Race || raceOccupancy.OccupiedIds.Count < 10 || raceOccupancy.OccupiedIds.Contains(reservedRace) || reservedRace is < 1 or > 31 ||
    !customOccupancy.Complete || !customOccupancy.OccupiedIds.SequenceEqual([7u, 9u]) || incompleteItemOccupancy.Complete ||
    reservedCreature != 100_001 || reservedGameObject != 100_001 || reservedQuest != 100_001 ||
    CrucibleContentProjectService.LoadRegistry(contentProjectRoot).Reservations.Count != 8 || !Directory.Exists(Path.Combine(contentProjectRoot, "Staging")))
    throw new InvalidOperationException("Portable content-project ID reservations collided with occupied or previously reserved IDs.");
try { _ = CrucibleContentProjectService.ReserveIds(contentProjectRoot, ContentIdDomain.Class, 1, 32, [], "invalid class"); throw new InvalidOperationException("A WotLK class reservation exceeded the 32-bit class-mask range."); }
catch (ArgumentOutOfRangeException) { }
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

var queryExportRoot = Path.Combine(Path.GetTempPath(), $"crucible-query-export-{Guid.NewGuid():N}"); Directory.CreateDirectory(queryExportRoot);
try
{
    var queryResult = new SqlQueryResult(["id", "id", "payload", "notes"],
    [
        new object?[] { 17u, 18u, new byte[] { 0xCA, 0xFE }, "Quoted, value" },
        new object?[] { 17802u, 17803u, null, "Multi\nline" }
    ], -1, TimeSpan.FromMilliseconds(2));
    var queryCsv = Path.Combine(queryExportRoot, "result.csv"); var queryJson = Path.Combine(queryExportRoot, "result.jsonl"); var transfer = new SqlTransferService();
    var csvResult = await transfer.ExportQueryResultAsync(queryResult, queryCsv, SqlExportFormat.Csv); var jsonResult = await transfer.ExportQueryResultAsync(queryResult, queryJson, SqlExportFormat.JsonLines);
    var csvText = File.ReadAllText(queryCsv); var jsonLines = File.ReadAllLines(queryJson);
    using var firstJson = JsonDocument.Parse(jsonLines[0]); using var secondJson = JsonDocument.Parse(jsonLines[1]);
    if (csvResult.Rows != 2 || csvResult.Columns != 4 || jsonResult.Format != SqlExportFormat.JsonLines || !csvText.StartsWith("id,id_2,payload,notes", StringComparison.Ordinal) ||
        !csvText.Contains("0xCAFE", StringComparison.Ordinal) || !csvText.Contains("\\N", StringComparison.Ordinal) ||
        firstJson.RootElement.GetProperty("id_2").GetUInt32() != 18 || firstJson.RootElement.GetProperty("payload").GetString() != "0xCAFE" || secondJson.RootElement.GetProperty("payload").ValueKind != JsonValueKind.Null)
        throw new InvalidOperationException("Structured SQL query-result export did not preserve duplicate column identities, binary values, NULL, multiline text, or atomic formats.");
    try { await transfer.ExportQueryResultAsync(queryResult, queryCsv, SqlExportFormat.Csv); throw new InvalidOperationException("Query-result export silently overwrote an existing file."); }
    catch (IOException) { }
    using var queryCancelled = new CancellationTokenSource(); queryCancelled.Cancel(); var cancelledPath = Path.Combine(queryExportRoot, "cancelled.csv");
    try { await transfer.ExportQueryResultAsync(queryResult, cancelledPath, SqlExportFormat.Csv, cancellationToken: queryCancelled.Token); throw new InvalidOperationException("A cancelled query-result export unexpectedly completed."); }
    catch (OperationCanceledException) { }
    if (File.Exists(cancelledPath) || Directory.EnumerateFiles(queryExportRoot, "*.tmp").Any()) throw new InvalidOperationException("A cancelled query-result export published or retained a partial file.");
}
finally { if (Directory.Exists(queryExportRoot)) Directory.Delete(queryExportRoot, true); }

var desktopSourceRoot = Path.Combine(FindRepositoryRoot(AppContext.BaseDirectory), "src", "WoWCrucible.Desktop");
if (Directory.Exists(desktopSourceRoot))
{
    var desktopSources = Directory.EnumerateFiles(desktopSourceRoot, "*.cs", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
    var featureWindows = desktopSources.Where(pair => !Path.GetFileName(pair.Key).Equals("MainWindow.axaml.cs", StringComparison.OrdinalIgnoreCase) &&
        (pair.Value.Contains(": Window", StringComparison.Ordinal) || pair.Value.Contains("new Window", StringComparison.Ordinal) || pair.Value.Contains(".ShowDialog(", StringComparison.Ordinal) || pair.Value.Contains(".Show(", StringComparison.Ordinal))).Select(pair => Path.GetRelativePath(desktopSourceRoot, pair.Key)).ToArray();
    var rigidConstraints = desktopSources.Where(pair => pair.Value.Contains("MinWidth =", StringComparison.Ordinal) || pair.Value.Contains("MaxWidth =", StringComparison.Ordinal) || pair.Value.Contains("MinHeight =", StringComparison.Ordinal) || pair.Value.Contains("MaxHeight =", StringComparison.Ordinal)).Select(pair => Path.GetRelativePath(desktopSourceRoot, pair.Key)).ToArray();
    var permanentHorizontalSplitters = desktopSources.Where(pair => !Path.GetFileName(pair.Key).Equals("ResponsiveSplitGrid.cs", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(pair.Value, @"new\s+GridSplitter\s*\{[^}]*ResizeDirection\s*=\s*GridResizeDirection\.Columns", RegexOptions.CultureInvariant)).Select(pair => Path.GetRelativePath(desktopSourceRoot, pair.Key)).ToArray();
    var responsiveSplitters = desktopSources.Sum(pair => Regex.Matches(pair.Value, @"new\s+ResponsiveSplitGrid\s*\(", RegexOptions.CultureInvariant).Count);
    var markupConstraints = Directory.EnumerateFiles(desktopSourceRoot, "*.axaml", SearchOption.AllDirectories).Where(path =>
    {
        var markup = File.ReadAllText(path); return markup.Contains(" MinWidth=", StringComparison.Ordinal) || markup.Contains(" MaxWidth=", StringComparison.Ordinal) || markup.Contains(" MinHeight=", StringComparison.Ordinal) || markup.Contains(" MaxHeight=", StringComparison.Ordinal) || markup.Contains(" Width=\"", StringComparison.Ordinal) && !markup.Contains("d:DesignWidth=", StringComparison.Ordinal) || markup.Contains(" Height=\"", StringComparison.Ordinal) && !markup.Contains("d:DesignHeight=", StringComparison.Ordinal);
    }).Select(path => Path.GetRelativePath(desktopSourceRoot, path)).ToArray();
    if (featureWindows.Length > 0 || rigidConstraints.Length > 0 || markupConstraints.Length > 0 || permanentHorizontalSplitters.Length > 0 || responsiveSplitters < 15)
        throw new InvalidOperationException($"Single-window/responsive desktop contract regressed. Feature windows: {string.Join(", ", featureWindows)}; C# rigid constraints: {string.Join(", ", rigidConstraints)}; markup constraints: {string.Join(", ", markupConstraints)}; permanent horizontal splitters: {string.Join(", ", permanentHorizontalSplitters)}; responsive splitter uses: {responsiveSplitters}.");
}

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
var itemIdBatch = ItemIdQueryParser.Parse("17 17802");
if (!itemIdBatch.SequenceEqual(new uint[] { 17, 17802 }) ||
    !ItemIdQueryParser.Parse("#17 #17802").SequenceEqual(itemIdBatch) ||
    !ItemIdQueryParser.Parse("17; 17802").SequenceEqual(itemIdBatch) ||
    !ItemIdQueryParser.TryParseSingle("17,802", out var groupedItemId) || groupedItemId != 17802 ||
    ItemIdQueryParser.TryParseSingle("17 17802", out _))
    throw new InvalidOperationException("Item ID query parsing confused exact batches with grouped single IDs.");
var lootGraph = new ItemCatalogService.LootReachabilityData(
    new Dictionary<string, IReadOnlyList<ItemCatalogService.LootRow>>(StringComparer.OrdinalIgnoreCase)
    {
        ["creature_loot_template"] = [new(10, 100, 0), new(10, 1, 900), new(99, 101, 0)],
        ["item_loot_template"] = [new(100, 102, 0)],
        ["disenchant_loot_template"] = [new(77, 106, 0)],
        ["spell_loot_template"] = [new(500, 103, 0)]
    },
    new Dictionary<uint, IReadOnlyList<ItemCatalogService.LootRow>>
    {
        [900] = [new(900, 104, 0)],
        [901] = [new(901, 105, 0)]
    },
    new Dictionary<string, IReadOnlySet<uint>>(StringComparer.OrdinalIgnoreCase)
    {
        ["creature_loot_template"] = new HashSet<uint> { 10 }
    },
    new Dictionary<uint, uint> { [102] = 77 });
var graphAcquired = new Dictionary<uint, HashSet<string>>(); var graphRejected = new Dictionary<uint, HashSet<string>>();
ItemCatalogService.ApplyReachableLoot(lootGraph, graphAcquired, new HashSet<uint> { 500 }, graphRejected, CancellationToken.None);
if (!new uint[] { 100, 102, 103, 104, 106 }.All(graphAcquired.ContainsKey) || graphAcquired.ContainsKey(1) || graphAcquired.ContainsKey(101) || graphAcquired.ContainsKey(105) ||
    !graphRejected.ContainsKey(101) || !graphRejected.ContainsKey(105) || graphRejected.ContainsKey(1))
    throw new InvalidOperationException("Source-aware loot closure did not distinguish reachable owners, dynamic item/disenchant/spell edges, nested reference pools, and orphaned rows.");
var startingItems = ItemCatalogService.ReadCharStartOutfitItems(Path.Combine(args[1], "CharStartOutfit.dbc"));
if (!startingItems.Contains(6948) || startingItems.Contains(17) || startingItems.Contains(17802))
    throw new InvalidOperationException("CharStartOutfit DBC acquisition coverage did not distinguish starting equipment from cut/developer items.");
var creatureDisplayFile = WdbcFile.Load(Path.Combine(args[1], "CreatureDisplayInfo.dbc"));
var creatureModelFile = WdbcFile.Load(Path.Combine(args[1], "CreatureModelData.dbc"));
var creatureSchemas = DbcSchemaCatalog.Load(args[0]);
var creatureDisplayColumns = creatureSchemas.ResolveColumns("CreatureDisplayInfo", creatureDisplayFile.FieldCount).Columns;
var creatureModelColumns = creatureSchemas.ResolveColumns("CreatureModelData", creatureModelFile.FieldCount).Columns;
var creatureDisplayIdColumn = creatureDisplayColumns.First(column => column.Name.Equals("ID", StringComparison.OrdinalIgnoreCase));
var creatureDisplayModelColumn = creatureDisplayColumns.First(column => column.Name.Equals("ModelID", StringComparison.OrdinalIgnoreCase));
var creatureModelIdColumn = creatureModelColumns.First(column => column.Name.Equals("ID", StringComparison.OrdinalIgnoreCase));
var availableCreatureModels = Enumerable.Range(0, creatureModelFile.RowCount).Select(row => creatureModelFile.GetRaw(row, creatureModelIdColumn)).ToHashSet();
var creatureDisplayRow = Enumerable.Range(0, creatureDisplayFile.RowCount).First(row => availableCreatureModels.Contains(creatureDisplayFile.GetRaw(row, creatureDisplayModelColumn)));
var creatureDisplayId = creatureDisplayFile.GetRaw(creatureDisplayRow, creatureDisplayIdColumn);
var creatureDisplayService = new CreatureDisplayPreviewService();
var creatureDisplay = creatureDisplayService.ResolveDisplay(args[1], args[0], creatureDisplayId);
if (creatureDisplay.DisplayId != creatureDisplayId || creatureDisplay.ModelId == 0 || !creatureDisplay.ModelClientPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || creatureDisplay.TextureVariations.Count != 3)
    throw new InvalidOperationException("Creature display preview did not resolve the build-12340 display/model/texture chain.");
var modelCreatureDisplays = creatureDisplayService.ResolveModelDisplays(args[1], args[0], Path.ChangeExtension(creatureDisplay.ModelClientPath, ".mdx"));
if (!modelCreatureDisplays.Any(display => display.DisplayId == creatureDisplayId) || modelCreatureDisplays.Any(display => !display.ModelClientPath.Equals(creatureDisplay.ModelClientPath, StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Creature model-path appearance lookup did not normalize MDX/M2 paths and return every matching display record.");
var creatureLibrary = Path.Combine(Path.GetTempPath(), $"crucible-creature-display-{Guid.NewGuid():N}");
var creatureProvenance = "fixture-source";
var creatureModelDirectory = Path.Combine(creatureLibrary, "Archives", "Content", Path.GetDirectoryName(creatureDisplay.ModelClientPath) ?? string.Empty, creatureProvenance);
Directory.CreateDirectory(creatureModelDirectory);
File.WriteAllBytes(Path.Combine(creatureModelDirectory, Path.GetFileName(creatureDisplay.ModelClientPath)), [1]);
File.WriteAllBytes(Path.Combine(creatureModelDirectory, Path.GetFileNameWithoutExtension(creatureDisplay.ModelClientPath) + "00.skin"), [2]);
foreach (var texture in creatureDisplay.TextureVariations.Where(path => !string.IsNullOrWhiteSpace(path)))
{
    var directory = Path.Combine(creatureLibrary, "Archives", "Content", Path.GetDirectoryName(texture) ?? string.Empty, creatureProvenance);
    Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, Path.GetFileName(texture)), [3]);
}
var sourcedCreatureDisplay = creatureDisplayService.ResolveDisplay(args[1], args[0], creatureDisplayId, creatureLibrary);
if (sourcedCreatureDisplay.Sources.Count != 1 || !sourcedCreatureDisplay.Sources[0].Ready || sourcedCreatureDisplay.Sources[0].Provenance != creatureProvenance)
    throw new InvalidOperationException("Creature display preview did not preserve same-provenance M2/SKIN source resolution.");
var sourcedModelDisplays = creatureDisplayService.ResolveModelDisplays(args[1], args[0], creatureDisplay.ModelClientPath, creatureLibrary);
if (sourcedModelDisplays.Single(display => display.DisplayId == creatureDisplayId).Sources.Single().CreatureTextures.Count != creatureDisplay.TextureVariations.Count(path => !string.IsNullOrWhiteSpace(path)))
    throw new InvalidOperationException("Creature model-path appearance lookup did not retain same-provenance texture variations.");
var provenanceDbcRoot = Path.Combine(creatureLibrary, "Archives", "Content", "DBFilesClient", creatureProvenance);
Directory.CreateDirectory(provenanceDbcRoot);
File.Copy(Path.Combine(args[1], "CreatureDisplayInfo.dbc"), Path.Combine(provenanceDbcRoot, "creaturedisplayinfo.dbc"));
File.Copy(Path.Combine(args[1], "CreatureModelData.dbc"), Path.Combine(provenanceDbcRoot, "creaturemodeldata.dbc"));
var emptyTargetDbcRoot = Path.Combine(creatureLibrary, "empty-target-dbc"); Directory.CreateDirectory(emptyTargetDbcRoot);
var provenanceLookup = creatureDisplayService.ResolveModelDisplaysForProvenance(emptyTargetDbcRoot, args[0], creatureDisplay.ModelClientPath, creatureLibrary, creatureProvenance);
if (!provenanceLookup.FromProcessedProvenance || provenanceLookup.Displays.All(display => display.DisplayId != creatureDisplayId) || provenanceLookup.DbcRoot is null)
    throw new InvalidOperationException("Creature appearance lookup did not fall back to the selected model's exact processed provenance DBC pair.");
var invalidTargetDbcRoot = Path.Combine(creatureLibrary, "invalid-target-dbc"); Directory.CreateDirectory(invalidTargetDbcRoot);
File.Copy(Path.Combine(args[1], "CreatureDisplayInfo.dbc"), Path.Combine(invalidTargetDbcRoot, "CreatureDisplayInfo.dbc"));
var invalidTargetModelPath = Path.Combine(invalidTargetDbcRoot, "CreatureModelData.dbc"); File.Copy(Path.Combine(args[1], "CreatureModelData.dbc"), invalidTargetModelPath);
var invalidTargetModel = File.ReadAllBytes(invalidTargetModelPath); BitConverter.GetBytes(27u).CopyTo(invalidTargetModel, 8); File.WriteAllBytes(invalidTargetModelPath, invalidTargetModel);
var invalidTargetLookup = creatureDisplayService.ResolveModelDisplaysForProvenance(invalidTargetDbcRoot, args[0], creatureDisplay.ModelClientPath, creatureLibrary, creatureProvenance);
if (!invalidTargetLookup.FromProcessedProvenance || invalidTargetLookup.Displays.All(display => display.DisplayId != creatureDisplayId) || !invalidTargetLookup.Finding.Contains("not compatible", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("An incompatible target creature DBC prevented safe exact-provenance source fallback or lost its diagnostic.");
Directory.Delete(creatureLibrary, true);
if (ItemCatalogEntry.ClassifyReviewGroup("Martin Fury", false, 6) != ItemAcquisitionReviewGroup.DeprecatedTestOrDeveloper ||
    ItemCatalogEntry.ClassifyReviewGroup("Thunderfury, Blessed Blade of the Windseeker?", false, 7, ["Ignored · quest 7561 is disabled (Deprecated quest)."] ) != ItemAcquisitionReviewGroup.DeprecatedTestOrDeveloper ||
    ItemCatalogEntry.ClassifyReviewGroup("Unfamiliar custom item", false, 4, ["No accepted acquisition row was found."]) != ItemAcquisitionReviewGroup.OtherManualReview ||
    ItemCatalogEntry.ClassifyReviewGroup("NPC Equip 50505", false) != ItemAcquisitionReviewGroup.NpcOrMonsterEquipment ||
    ItemCatalogEntry.ClassifyReviewGroup("Deprecated Old Belt", false) != ItemAcquisitionReviewGroup.DeprecatedTestOrDeveloper ||
    ItemCatalogEntry.ClassifyReviewGroup("Anything", true, 6, ["developer"]) != ItemAcquisitionReviewGroup.KnownAcquisition)
    throw new InvalidOperationException("No-path item review grouping hid or mislabeled exact developer/cut-item regression cases.");
var spellCreation = ItemCatalogService.ReadSpellCreationGraph(Path.Combine(args[1], "Spell.dbc"));
if (!spellCreation.TryGetValue(597, out var conjureFood) || !conjureFood.CreatedItems.Contains(1113u) || spellCreation.Count < 1_000)
    throw new InvalidOperationException("Spell.dbc acquisition coverage did not map reachable create-item effects such as Conjured Bread.");
if (!SqlWorkspaceService.IsReadOnlyStatement("-- reviewed\nSELECT * FROM item_template") || SqlWorkspaceService.IsReadOnlyStatement("/* not read-only */ UPDATE item_template SET name='bad'"))
    throw new InvalidOperationException("SQL Studio read-only routing could execute a write through the immediate query path.");
var readBatchSql = "SELECT 'semi;colon' AS value; -- retained comment\nSHOW TABLES;";
var readStatements = SqlReadBatchParser.Split(readBatchSql);
if (readStatements.Count != 2 || !SqlWorkspaceService.IsReadOnlyBatch(readBatchSql) || SqlWorkspaceService.IsReadOnlyStatement(readBatchSql) ||
    !SqlWorkspaceService.IsReadOnlyStatement("SELECT 'INTO OUTFILE' AS harmless_text") ||
    SqlWorkspaceService.IsReadOnlyBatch("SELECT 1; UPDATE item_template SET name='bad'") ||
    SqlWorkspaceService.IsReadOnlyStatement("SELECT * FROM item_template INTO /* no */ OUTFILE '/tmp/leak'") ||
    SqlReadBatchParser.Split("SELECT `semi;column`, \"double;value\" FROM item_template;").Count != 1)
    throw new InvalidOperationException("SQL read-batch splitting did not preserve quoted semicolons or independently reject write/file-output statements.");
try { _ = SqlReadBatchParser.Split("SELECT 'unterminated"); throw new InvalidOperationException("An unterminated SQL literal was accepted."); }
catch (InvalidDataException exception) when (exception.Message.Contains("quoted", StringComparison.OrdinalIgnoreCase)) { }
var favoriteFixture = new SqlRowFavorite("acore_world", "item_template", new Dictionary<string, string?> { ["entry"] = "17802" }, "Thunderfury review", "Deprecated quest reward; restore usable stats", DateTimeOffset.UnixEpoch, @"C:\dbc\Item.dbc", @"C:\client\Data\patch-X.MPQ");
if (!SqlFavoriteWorkspaceService.Matches(favoriteFixture, "thunderfury 17802") ||
    !SqlFavoriteWorkspaceService.Matches(favoriteFixture, "deprecated patch-x") ||
    SqlFavoriteWorkspaceService.Matches(favoriteFixture, "creature 17802") ||
    SqlFavoriteWorkspaceService.ParseStoredKey(new("guid", "binary", "binary(16)", false, null, "PRI", string.Empty, 1), "0x00112233445566778899AABBCCDDEEFF") is not byte[] parsedFavoriteGuid ||
    Convert.ToHexString(parsedFavoriteGuid) != "00112233445566778899AABBCCDDEEFF")
    throw new InvalidOperationException("SQL favorite search or byte-safe primary-key restoration regressed.");
var favoriteStoreRoot = Path.Combine(Path.GetTempPath(), $"crucible-favorite-store-{Guid.NewGuid():N}"); Directory.CreateDirectory(favoriteStoreRoot);
try
{
    var favoriteStorePath = Path.Combine(favoriteStoreRoot, "favorites.json");
    File.WriteAllText(favoriteStorePath, """[{"database":"acore_world","table":"item_template","key":{"entry":"17802"},"label":"Thunderfury review","notes":"portable","addedUtc":"2026-07-18T00:00:00Z","dbcPath":null,"mpqPath":null}]""");
    var portableFavorites = SqlFavoriteStore.Load(favoriteStorePath);
    if (portableFavorites.Count != 1 || portableFavorites[0].Key["entry"] != "17802" || SqlFavoriteStore.Save(portableFavorites[0] with { Notes = "updated" }, favoriteStorePath).Single().Notes != "updated")
        throw new InvalidOperationException("Portable SQL favorite loading did not accept case-insensitive JSON or atomically update metadata.");
    File.WriteAllText(favoriteStorePath, "{broken");
    if (SqlFavoriteStore.Load(favoriteStorePath).Count != 0 || Directory.EnumerateFiles(favoriteStoreRoot, "favorites.json.corrupt-*.json").Count() != 1)
        throw new InvalidOperationException("A corrupt SQL favorites file was discarded instead of being preserved before recovery.");
}
finally { if (Directory.Exists(favoriteStoreRoot)) Directory.Delete(favoriteStoreRoot, true); }
var queryHistoryRoot = Path.Combine(Path.GetTempPath(), $"crucible-query-history-{Guid.NewGuid():N}"); Directory.CreateDirectory(queryHistoryRoot);
try
{
    var store = new SqlQueryHistoryStore(Path.Combine(queryHistoryRoot, "history.json"), 10);
    var batch = new SqlQueryBatch([new SqlQueryBatchResult(1, "SELECT 1", new SqlQueryResult(["value"], [[1]], -1, TimeSpan.FromMilliseconds(2)), false)], TimeSpan.FromMilliseconds(3));
    var recorded = store.Record("acore_world", "SELECT 1", batch); var bookmarked = store.Bookmark("acore_world", "SELECT 1", "Health check");
    for (var index = 0; index < 14; index++) store.Record("acore_world", $"SELECT {index + 2}", batch);
    var retained = store.Load();
    if (recorded.Count != 1 || bookmarked.Count != 1 || retained.Count(entry => entry.Bookmarked) != 1 || retained.Count(entry => !entry.Bookmarked) != 10 ||
        retained.Single(entry => entry.Bookmarked).Label != "Health check" || store.ClearUnbookmarked().Count != 1)
        throw new InvalidOperationException("Portable SQL query history did not deduplicate, bookmark, cap, or clear entries safely.");
    File.WriteAllText(Path.Combine(queryHistoryRoot, "history.json"), "{broken");
    if (store.Load().Count != 0 || Directory.EnumerateFiles(queryHistoryRoot, "history.json.corrupt-*.json").Count() != 1)
        throw new InvalidOperationException("A corrupt SQL query-history file was discarded instead of being preserved before recovery.");
}
finally { if (Directory.Exists(queryHistoryRoot)) Directory.Delete(queryHistoryRoot, true); }
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
var fixtureView = new SqlDatabaseObjectInfo(SqlDatabaseObjectType.View, "acore_world", "crucible_items", "fixture@localhost", "security DEFINER");
var fixtureEvent = new SqlDatabaseObjectInfo(SqlDatabaseObjectType.Event, "acore_world", "crucible_tick", "fixture@localhost", "recurring", State: "ENABLED");
var viewSql = SqlDatabaseObjectService.BuildCreateOrReplaceViewSql("acore_world", "crucible_items", "-- exact\nSELECT 'semi;colon' AS value");
if (!viewSql.StartsWith("CREATE OR REPLACE VIEW `acore_world`.`crucible_items`", StringComparison.Ordinal) ||
    SqlDatabaseObjectService.BuildDropSql(fixtureView) != "DROP VIEW `acore_world`.`crucible_items`;" ||
    SqlDatabaseObjectService.BuildEventStateSql(fixtureEvent, false) != "ALTER EVENT `acore_world`.`crucible_tick` DISABLE;")
    throw new InvalidOperationException("Guided database-object SQL did not preserve exact qualified identity or quoted SELECT content.");
try { _ = SqlDatabaseObjectService.BuildCreateOrReplaceViewSql("acore_world", "bad", "SELECT 1; DROP TABLE item_template"); throw new InvalidOperationException("Guided view creation accepted a second statement."); }
catch (InvalidDataException exception) when (exception.Message.Contains("exactly one SELECT", StringComparison.OrdinalIgnoreCase)) { }
try { _ = SqlDatabaseObjectService.BuildCreateOrReplaceViewSql("acore_world", "bad", "SELECT * FROM item_template INTO OUTFILE '/tmp/leak'"); throw new InvalidOperationException("Guided view creation accepted SELECT file output."); }
catch (InvalidDataException exception) when (exception.Message.Contains("exactly one SELECT", StringComparison.OrdinalIgnoreCase)) { }
var syncKey = new[] { new LegacyDatabaseAuditKeyPart("entry", new(LegacyDatabaseAuditValueState.Scalar, "17")) };
var syncFields = new[] { new DatabaseSyncField("name", new(LegacyDatabaseAuditValueState.Scalar, "Before"), new(LegacyDatabaseAuditValueState.Scalar, "After")) };
var syncOperation = new DatabaseSyncOperation("item_template", LegacyDatabaseContentDomain.ItemsAndSets, LegacyDatabaseRowChangeKind.Modified, syncKey, syncFields, DatabaseSyncOperationStatus.Ready, "fixture");
var syncPlan = new DatabaseSyncPlan(DatabaseSynchronizationService.PlanFormat, DatabaseSynchronizationService.FormatVersion, "fixture", DateTimeOffset.UnixEpoch, new string('a', 64), new("127.0.0.1", 3306, "acore", "acore_world", "fixture"), false, [], [new("item_template", "entry", 17, 90017, 3)], [syncOperation], new string('b', 64), []);
var syncPreview = new DatabaseSynchronizationService().PreviewSql(syncPlan);
if (!syncPreview.Contains("ID REMAP: item_template.entry 17 -> 90017; 3 recognized", StringComparison.Ordinal) || !syncPreview.Contains("UPDATE `item_template`", StringComparison.Ordinal) || !syncPreview.Contains("`entry` <=>", StringComparison.Ordinal) || !syncPreview.Contains("`name` <=>", StringComparison.Ordinal) || !syncPreview.EndsWith("ROLLBACK;" + Environment.NewLine, StringComparison.Ordinal) || syncPreview.Contains("COMMIT;", StringComparison.Ordinal))
    throw new InvalidOperationException("Database synchronization preview no longer preserves keys, preimages, or the non-committing review boundary.");
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
var portablePetStats = BehaviorAuthoringAdapter.PortableTable("pet_levelstats");
var portablePetNames = BehaviorAuthoringAdapter.PortableTable("pet_name_generation");
var portablePetNameLocales = BehaviorAuthoringAdapter.PortableTable("pet_name_generation_locale");
var portablePetAuras = BehaviorAuthoringAdapter.PortableTable("spell_pet_auras");
if (behaviorDomains.Count != 13 || portableGossipOption.Columns.Count != 14 || portableNpcText.Columns.Count != 90 || portableCondition.Columns.Count != 15 || portableSmart.Columns.Count != 31 || portablePetStats.Columns.Count != 12 || portablePetNames.Columns.Count != 4 || portablePetNameLocales.Columns.Count != 5 || portablePetAuras.Columns.Count != 4)
    throw new InvalidOperationException("Behavior portable schema coverage drifted from the current WotLK world tables.");
var petStatsValues = new Dictionary<string, object?>(BehaviorAuthoringAdapter.Defaults(portablePetStats), StringComparer.OrdinalIgnoreCase) { ["creature_entry"] = 416, ["level"] = 80, ["hp"] = 10000, ["min_dmg"] = 305, ["max_dmg"] = 458 };
var petStatsPlan = BehaviorAuthoringAdapter.CreatePlan(BehaviorDomainCatalog.Find("pet-level-stats"), portablePetStats, petStatsValues);
var petAuraValues = new Dictionary<string, object?>(BehaviorAuthoringAdapter.Defaults(portablePetAuras), StringComparer.OrdinalIgnoreCase) { ["spell"] = 35691, ["effectId"] = 0, ["pet"] = 17252, ["aura"] = 35696 };
var petAuraPlan = BehaviorAuthoringAdapter.CreatePlan(BehaviorDomainCatalog.Find("spell-pet-aura"), portablePetAuras, petAuraValues);
if (petStatsPlan.Rows[0].Key.Count != 2 || petAuraPlan.Rows[0].Key.Count != 3 || BehaviorSemanticCatalog.PetNameHalves.Single(choice => choice.Value == 1).Name != "Second half" || !petAuraPlan.PreviewSql().Contains("35696", StringComparison.Ordinal))
    throw new InvalidOperationException("Pet complete-field planning, composite identities, decoded name halves, or SQL preview failed.");
var customPetStats = portablePetStats with { Columns = portablePetStats.Columns.Concat([new DatabaseColumnCapability("custom_growth_bucket", "int", "int unsigned", false, "0", "", "", 13)]).ToArray() };
var petCurveSource = Enumerable.Range(1, 3).Select(level => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
{
    ["creature_entry"] = 416, ["level"] = level, ["hp"] = level == 3 ? 31 : level * 10, ["mana"] = level * 5, ["armor"] = level * 20,
    ["str"] = 10 + level, ["agi"] = 11 + level, ["sta"] = 12 + level, ["inte"] = 13 + level, ["spi"] = 14 + level,
    ["min_dmg"] = level, ["max_dmg"] = level + 2, ["custom_growth_bucket"] = 77
}).ToArray();
var curveService = new PetLevelCurveService(); var curveRequest = new PetLevelCurveRequest(416, 990416, 1, 3, new(1.5m, 0m, 1m, 2m, 1.25m)); var curvePlan = curveService.CreateScaledPlan(customPetStats, curveRequest, petCurveSource);
var finalCurveValues = curvePlan.Rows[2].Values;
if (curvePlan.Rows.Count != 3 || Convert.ToUInt32(curvePlan.Rows[0].Values["creature_entry"], CultureInfo.InvariantCulture) != 990416 || Convert.ToDecimal(finalCurveValues["hp"], CultureInfo.InvariantCulture) != 47m || Convert.ToDecimal(finalCurveValues["mana"], CultureInfo.InvariantCulture) != 0m || Convert.ToDecimal(finalCurveValues["str"], CultureInfo.InvariantCulture) != 26m || Convert.ToDecimal(finalCurveValues["custom_growth_bucket"], CultureInfo.InvariantCulture) != 77m)
    throw new InvalidOperationException("Pet curve cloning did not preserve the source shape, scale known stat families, round integral fields, or retain custom columns.");
var preparedCurve = new PetLevelCurvePreparedPlan(curveRequest, curvePlan, "schema", new Dictionary<int, string?> { [1] = null, [2] = null, [3] = null }, 3);
if (!curveService.PreviewSql(preparedCurve, PetLevelCurveWriteMode.InsertMissing).Contains("WHERE NOT EXISTS", StringComparison.Ordinal) || !curveService.PreviewSql(preparedCurve, PetLevelCurveWriteMode.UpdateExactRange).Contains("UPDATE `pet_levelstats` SET", StringComparison.Ordinal))
    throw new InvalidOperationException("Pet curve SQL previews no longer distinguish missing-only from explicit exact-range updates.");
var curveComparison = curveService.CreateComparison(customPetStats, new(416, 990416, 1, 3), petCurveSource, curvePlan.Rows.Select(row => row.Values).ToArray()); var hpComparison = curveComparison.Metrics.Single(metric => metric.Column == "hp");
if (curveComparison.Metrics.Count != 11 || hpComparison.Points.Count != 3 || hpComparison.PairedLevels != 3 || hpComparison.LeftGrowthPercent != 210m || decimal.Round(hpComparison.EndDeltaPercent ?? 0, 3) != 51.613m || curveComparison.MissingLeftLevels.Count != 0 || curveComparison.MissingRightLevels.Count != 0)
    throw new InvalidOperationException("Pet family curve comparison lost numeric/custom metrics, per-level values, normalized growth, or coverage reporting.");
var incompleteComparison = curveService.CreateComparison(customPetStats, new(416, 990416, 1, 3), petCurveSource, curvePlan.Rows.Take(2).Select(row => row.Values).ToArray());
if (!incompleteComparison.MissingRightLevels.SequenceEqual([3]) || incompleteComparison.Metrics.Single(metric => metric.Column == "hp").EndDeltaPercent is not null)
    throw new InvalidOperationException("Pet family comparison hid a missing endpoint or reported a fabricated end delta.");
try { _ = curveService.CreateScaledPlan(customPetStats, curveRequest with { EndLevel = 4 }, petCurveSource); throw new InvalidOperationException("A pet curve with a missing source level was accepted."); }
catch (InvalidDataException) { }
try { var invalid = new Dictionary<string, object?>(petStatsValues, StringComparer.OrdinalIgnoreCase) { ["min_dmg"] = 459, ["max_dmg"] = 458 }; _ = BehaviorAuthoringAdapter.CreatePlan(BehaviorDomainCatalog.Find("pet-level-stats"), portablePetStats, invalid); throw new InvalidOperationException("Invalid pet damage range was accepted."); }
catch (InvalidDataException) { }
try { var invalid = new Dictionary<string, object?>(petAuraValues, StringComparer.OrdinalIgnoreCase) { ["effectId"] = 3 }; _ = BehaviorAuthoringAdapter.CreatePlan(BehaviorDomainCatalog.Find("spell-pet-aura"), portablePetAuras, invalid); throw new InvalidOperationException("Invalid fourth WotLK pet-aura effect slot was accepted."); }
catch (InvalidDataException) { }
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

var generatedImportFile = WdbcFile.Load(generatedKeyPath); var generatedImportOriginalHash = generatedImportFile.ComputeContentSha256();
var generatedBefore = Convert.ToSingle(generatedImportFile.GetDisplayValue(900, generatedData), System.Globalization.CultureInfo.InvariantCulture);
var generatedImportCsv = Path.Combine(Path.GetTempPath(), $"crucible-gt-import-{Guid.NewGuid():N}.csv");
File.WriteAllText(generatedImportCsv, $"$recordKey,$rowIndex,Data\r\n900,900,{(generatedBefore + 0.25f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\r\n1100,,{(generatedBefore + 0.5f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\r\n");
try
{
    _ = DbcRowImportService.Preview(generatedImportFile, generatedSchema, generatedImportCsv, new(DbcRowImportFormat.Csv));
    throw new InvalidOperationException("DBC import appended a missing virtual record without explicit permission.");
}
catch (InvalidDataException exception) when (exception.Message.Contains("Enable append explicitly", StringComparison.Ordinal)) { }
var generatedImportPlan = DbcRowImportService.Preview(generatedImportFile, generatedSchema, generatedImportCsv, new(DbcRowImportFormat.Csv, AllowAppend: true));
if (generatedImportPlan.UpdatedRows != 1 || generatedImportPlan.AppendedRows != 1 || generatedImportPlan.ChangedCells != 2 || generatedImportFile.ComputeContentSha256() != generatedImportOriginalHash)
    throw new InvalidOperationException("DBC import preview mutated its source or misreported virtual updates/appends.");
var generatedApply = DbcRowImportService.Apply(generatedImportFile, generatedImportPlan);
if (generatedApply.ResultRows != 1101 || generatedImportFile.RowCount != 1101 || Math.Abs(Convert.ToSingle(generatedImportFile.GetDisplayValue(900, generatedData)) - (generatedBefore + 0.25f)) > 0.0001f ||
    Math.Abs(Convert.ToSingle(generatedImportFile.GetDisplayValue(1100, generatedData)) - (generatedBefore + 0.5f)) > 0.0001f)
    throw new InvalidOperationException("DBC import did not apply virtual updates and contiguous append without writing the synthetic key into physical data.");

var spellImportFile = WdbcFile.Load(Path.Combine(args[1], "Spell.dbc")); var spellImportHash = spellImportFile.ComputeContentSha256();
var spellImportJson = Path.Combine(Path.GetTempPath(), $"crucible-spell-import-{Guid.NewGuid():N}.json");
File.WriteAllText(spellImportJson, "[{\"$recordKey\":133,\"Name_Lang[enUS]\":\"  Crucible, Fireball  \"}]");
var spellImportPlan = DbcRowImportService.Preview(spellImportFile, spellExportSchema, spellImportJson, new(DbcRowImportFormat.Json));
if (spellImportPlan.UpdatedRows != 1 || spellImportPlan.AppendedRows != 0 || spellImportPlan.ChangedCells != 1 || spellImportFile.ComputeContentSha256() != spellImportHash)
    throw new InvalidOperationException("Physical-key DBC import preview mutated the Spell source or misreported its change.");
DbcRowImportService.Apply(spellImportFile, spellImportPlan);
var spellRows = DbcRecordIdentity.IndexRows(spellImportFile, spellExportSchema.Columns, spellExportSchema.KeyStrategy); var spellName = spellExportSchema.Columns.Single(column => column.Name == "Name_Lang[enUS]");
if (spellImportFile.GetRaw(spellRows[133], spellExportSchema.Columns[0]) != 133 || spellImportFile.GetString(spellImportFile.GetRaw(spellRows[133], spellName)) != "  Crucible, Fireball  ")
    throw new InvalidOperationException("DBC import changed a physical key or failed to preserve/re-intern decoded string whitespace.");
var quotedCsvImport = Path.Combine(Path.GetTempPath(), $"crucible-spell-quoted-{Guid.NewGuid():N}.csv");
File.WriteAllText(quotedCsvImport, "$recordKey,Name_Lang[enUS]\r\n133,\"Crucible, Fireball\r\nSecond line\"\r\n");
var quotedCsvFile = WdbcFile.Load(Path.Combine(args[1], "Spell.dbc")); var quotedCsvPlan = DbcRowImportService.Preview(quotedCsvFile, spellExportSchema, quotedCsvImport, new(DbcRowImportFormat.Csv)); DbcRowImportService.Apply(quotedCsvFile, quotedCsvPlan);
var quotedRows = DbcRecordIdentity.IndexRows(quotedCsvFile, spellExportSchema.Columns, spellExportSchema.KeyStrategy);
if (quotedCsvFile.GetDisplayValue(quotedRows[133], spellName).ToString() != "Crucible, Fireball\r\nSecond line") throw new InvalidOperationException("DBC CSV import did not preserve quoted commas and embedded newlines.");
var physicalAppendJson = Path.Combine(Path.GetTempPath(), $"crucible-spell-append-{Guid.NewGuid():N}.json"); File.WriteAllText(physicalAppendJson, "[{\"$recordKey\":9999999,\"Name_Lang[enUS]\":\"Crucible Physical Append\"}]");
var physicalAppendFile = WdbcFile.Load(Path.Combine(args[1], "Spell.dbc")); var physicalAppendPlan = DbcRowImportService.Preview(physicalAppendFile, spellExportSchema, physicalAppendJson, new(DbcRowImportFormat.Json, AllowAppend: true)); DbcRowImportService.Apply(physicalAppendFile, physicalAppendPlan);
var physicalAppendRows = DbcRecordIdentity.IndexRows(physicalAppendFile, spellExportSchema.Columns, spellExportSchema.KeyStrategy);
if (physicalAppendPlan.AppendedRows != 1 || !physicalAppendRows.TryGetValue(9999999, out var physicalAppendedRow) || physicalAppendFile.GetDisplayValue(physicalAppendedRow, spellName).ToString() != "Crucible Physical Append")
    throw new InvalidOperationException("DBC import did not explicitly append and initialize a missing physical-key record.");

var unknownImport = Path.Combine(Path.GetTempPath(), $"crucible-spell-unknown-{Guid.NewGuid():N}.jsonl"); File.WriteAllText(unknownImport, "{\"$recordKey\":133,\"DefinitelyNotAColumn\":1}\n");
try { _ = DbcRowImportService.Preview(WdbcFile.Load(Path.Combine(args[1], "Spell.dbc")), spellExportSchema, unknownImport, new(DbcRowImportFormat.JsonLines)); throw new InvalidOperationException("DBC import silently discarded an unknown schema column."); }
catch (InvalidDataException exception) when (exception.Message.Contains("unknown column", StringComparison.OrdinalIgnoreCase)) { }
var duplicateImport = Path.Combine(Path.GetTempPath(), $"crucible-spell-duplicate-{Guid.NewGuid():N}.jsonl"); File.WriteAllText(duplicateImport, "{\"$recordKey\":133,\"Effect[0]\":1}\n{\"$recordKey\":133,\"Effect[0]\":2}\n");
try { _ = DbcRowImportService.Preview(WdbcFile.Load(Path.Combine(args[1], "Spell.dbc")), spellExportSchema, duplicateImport, new(DbcRowImportFormat.JsonLines)); throw new InvalidOperationException("DBC import accepted duplicate input targets."); }
catch (InvalidDataException exception) when (exception.Message.Contains("more than once", StringComparison.OrdinalIgnoreCase)) { }
var staleImportFile = WdbcFile.Load(Path.Combine(args[1], "Spell.dbc")); var stalePlan = DbcRowImportService.Preview(staleImportFile, spellExportSchema, spellImportJson, new(DbcRowImportFormat.Json));
staleImportFile.SetRaw(0, spellExportSchema.Columns[1], staleImportFile.GetRaw(0, spellExportSchema.Columns[1]) + 1);
try { _ = DbcRowImportService.Apply(staleImportFile, stalePlan); throw new InvalidOperationException("DBC import applied a preview after the open table changed."); }
catch (InvalidOperationException exception) when (exception.Message.Contains("changed after", StringComparison.OrdinalIgnoreCase)) { }
File.Delete(generatedImportCsv); File.Delete(spellImportJson); File.Delete(quotedCsvImport); File.Delete(physicalAppendJson); File.Delete(unknownImport); File.Delete(duplicateImport);
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
var deploymentBundleFixture = Path.Combine(Path.GetTempPath(), $"crucible-dbc-sql-bundle-{Guid.NewGuid():N}");
var deploymentBundleSource = Path.Combine(Path.GetTempPath(), $"crucible-gtregen-source-{Guid.NewGuid():N}.dbc");
var deploymentBundleServer = Path.Combine(Path.GetTempPath(), $"crucible-gtregen-server-{Guid.NewGuid():N}.dbc");
incidentDbc.Save(deploymentBundleSource, false); File.Copy(generatedKeyPath, deploymentBundleServer);
try
{
    var bundleAudit = incidentAudit with { DbcPath = deploymentBundleSource };
    var bundleService = new DbcSqlDeploymentBundleService();
    var bundle = bundleService.Create(deploymentBundleFixture, new("127.0.0.1", 3306, "fixture", "secret-never-serialized", "fixture_world"),
        bundleAudit, generatedSchema, args[0], deploymentBundleServer, Enumerable.Range(900, 100).Select(value => (uint)value));
    var loadedBundle = bundleService.Load(deploymentBundleFixture);
    var migrationText = File.ReadAllText(Path.Combine(deploymentBundleFixture, loadedBundle.Plan.MigrationSqlFile));
    var rollbackText = File.ReadAllText(Path.Combine(deploymentBundleFixture, loadedBundle.Plan.RollbackSqlFile));
    if (loadedBundle.Plan.Rows.Count != 100 || loadedBundle.Plan.Database.User != "fixture" || loadedBundle.Plan.Database.Database != "fixture_world" ||
        JsonSerializer.Serialize(loadedBundle.Plan).Contains("secret-never-serialized", StringComparison.Ordinal) ||
        !migrationText.Contains("(900,1", StringComparison.Ordinal) || !rollbackText.Contains("(900,0", StringComparison.Ordinal) ||
        !PatchManifestService.Validate(PatchManifestService.Load(Path.Combine(deploymentBundleFixture, loadedBundle.Plan.ClientManifestFile))).Passed)
        throw new InvalidOperationException("The synchronized DBC/SQL deployment bundle did not preserve its exact payload, SQL pre-image, or secret-free target identity.");
    var moduleFixture = Path.Combine(Path.GetTempPath(), $"crucible-module-{Guid.NewGuid():N}");
    var moduleMigration = bundleService.ExportModuleMigration(deploymentBundleFixture, moduleFixture);
    if (!File.Exists(moduleMigration) || !moduleMigration.Contains(Path.Combine("data", "sql", "db-world"), StringComparison.OrdinalIgnoreCase) || File.ReadAllText(moduleMigration) != migrationText)
        throw new InvalidOperationException("Portable AzerothCore module migration export did not preserve the reviewed bundle SQL.");
    Directory.Delete(moduleFixture, true);
    var staleBundleFixture = deploymentBundleFixture + "-stale";
    try
    {
        _ = bundleService.Create(staleBundleFixture, new("127.0.0.1", 3306, "fixture", "secret", "fixture_world"),
            incidentAudit, generatedSchema, args[0], deploymentBundleServer, Enumerable.Range(900, 100).Select(value => (uint)value));
        throw new InvalidOperationException("A deployment bundle was created after its audited DBC values changed.");
    }
    catch (InvalidDataException exception) when (exception.Message.Contains("changed after audit", StringComparison.OrdinalIgnoreCase)) { }
    finally { if (Directory.Exists(staleBundleFixture)) Directory.Delete(staleBundleFixture, true); }
    var migrationPath = Path.Combine(deploymentBundleFixture, loadedBundle.Plan.MigrationSqlFile); File.AppendAllText(migrationPath, "-- tampered");
    try { _ = bundleService.Load(deploymentBundleFixture); throw new InvalidOperationException("A changed deployment-bundle migration was accepted."); }
    catch (InvalidDataException exception) when (exception.Message.Contains("migration SQL changed", StringComparison.OrdinalIgnoreCase)) { }
}
finally
{
    if (Directory.Exists(deploymentBundleFixture)) Directory.Delete(deploymentBundleFixture, true);
    File.Delete(deploymentBundleSource); File.Delete(deploymentBundleServer);
}
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
    var stagedSpellIds = DbcRecordIdentity.IndexRows(spell, spellColumns, new(DbcRecordKeyKind.PhysicalColumn, 0)).Keys.ToArray();
    var stagedSpellOccupancy = await new ContentIdOccupancyService().InspectAsync(ContentIdDomain.Spell, null, null, null, null,
        inMemoryDbcIds: new Dictionary<string, IReadOnlyCollection<uint>>(StringComparer.OrdinalIgnoreCase) { ["Spell"] = stagedSpellIds });
    if (stagedSpellOccupancy.Complete || stagedSpellOccupancy.Sources.Single(source => source.Kind == "DBC") is not { Available: true } stagedSpellSource || stagedSpellSource.Ids != spell.RowCount)
        throw new InvalidOperationException("Staged in-memory Spell.dbc identities were not accepted while the missing SQL source remained correctly blocking.");
    var exactCloneTarget = checked(spell.NextId(spellColumns[0]) + 1_000u); var exactCloneRow = spell.CloneRowWithId(0, spellColumns[0], exactCloneTarget);
    if (spell.GetRaw(exactCloneRow, spellColumns[0]) != exactCloneTarget || spell.GetRaw(exactCloneRow, spellColumns[136]) != spell.GetRaw(0, spellColumns[136]))
        throw new InvalidOperationException("Exact project-reserved Spell.dbc cloning did not preserve the source row under the requested identity.");
    try { _ = spell.CloneRowWithId(0, spellColumns[0], exactCloneTarget); throw new InvalidOperationException("Exact Spell.dbc cloning accepted a duplicate project ID."); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)) { }
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

static void WriteMapChunk(BinaryWriter writer, string id, byte[] payload)
{
    if (id.Length != 4) throw new ArgumentException("A map chunk ID must contain four characters.", nameof(id));
    writer.Write(System.Text.Encoding.ASCII.GetBytes(new string(id.Reverse().ToArray()))); writer.Write((uint)payload.Length); writer.Write(payload);
}

static void WriteM2Track(byte[] target, int offset, int timestampSeriesOffset, int valueSeriesOffset)
{
    BitConverter.GetBytes((ushort)1).CopyTo(target, offset); BitConverter.GetBytes((short)-1).CopyTo(target, offset + 2);
    BitConverter.GetBytes((uint)1).CopyTo(target, offset + 4); BitConverter.GetBytes((uint)timestampSeriesOffset).CopyTo(target, offset + 8);
    BitConverter.GetBytes((uint)1).CopyTo(target, offset + 12); BitConverter.GetBytes((uint)valueSeriesOffset).CopyTo(target, offset + 16);
}

static string FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "WoWCrucible.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException($"Could not locate the WoW Crucible repository above {start}.");
}

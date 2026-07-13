using WoWCrucible.Core;
using System.Diagnostics;

if (args.Length != 2)
    throw new ArgumentException("Usage: WoWCrucible.Core.Tests <schema.xml> <dbc-directory>");

var schema = DbcSchemaCatalog.Load(args[0]);
var builtInSchema = DbcSchemaCatalog.CreateBuiltIn12340();
var builtInSpellColumns = builtInSchema.GetColumns("Spell", 234);
if (builtInSpellColumns[136].Name != "Name[enUS]" || builtInSpellColumns[233].Name != "SpellDifficultyID")
    throw new InvalidOperationException("Built-in Spell.dbc schema is incomplete.");
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
File.Delete(mpqOutput);
File.Delete(mpqOutput + ".bak");

Console.WriteLine($"PASS: loaded {loaded:N0} WDBC files, cloned 100 real spells in {spellBulkCloneMilliseconds:N0} ms, verified persistence, and created/updated/reopened a patch MPQ.");

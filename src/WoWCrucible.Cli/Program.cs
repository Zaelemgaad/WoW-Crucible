using WoWCrucible.Core;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Help();

try
{
    return args[0].ToLowerInvariant() switch
    {
        "dbc" => Dbc(args[1..]),
        "mpq" => Mpq(args[1..]),
        _ => Fail($"Unknown command: {args[0]}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static int Dbc(string[] args)
{
    if (args.Length != 2 || args[0] != "info") return Fail("Usage: wowcrucible dbc info <file.dbc>");
    var file = WdbcFile.Load(args[1]);
    Console.WriteLine($"Path\t{Path.GetFullPath(args[1])}");
    Console.WriteLine($"Rows\t{file.RowCount}"); Console.WriteLine($"Fields\t{file.FieldCount}"); Console.WriteLine($"StringBytes\t{file.StringTableSize}");
    return 0;
}

static int Mpq(string[] args)
{
    if (args.Length == 0) return Fail("Usage: wowcrucible mpq <list|extract|create|update> ...");
    var service = new PatchArchiveService();
    switch (args[0])
    {
        case "list" when args.Length is 2 or 3:
            {
                var query = args.Length == 3 ? args[2] : string.Empty;
                foreach (var file in service.ListFiles(args[1]).Where(file => file.ArchivePath.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"{file.Size}\t{file.CompressedSize}\t{file.ArchivePath}");
                return 0;
            }
        case "extract" when args.Length is 3 or 4:
            {
                var query = args.Length == 4 ? args[3] : string.Empty;
                var files = service.ListFiles(args[1]).Where(file => file.ArchivePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                service.Extract(args[1], args[2], files, new ConsoleProgress());
                return 0;
            }
        case "create" when args.Length >= 3:
            service.Create(args[1], PatchInputMapper.Map(args[2..])); return 0;
        case "update" when args.Length >= 3:
            service.Update(args[1], PatchInputMapper.Map(args[2..])); return 0;
        default:
            return Fail("Usage:\n  wowcrucible mpq list <archive.mpq> [filter]\n  wowcrucible mpq extract <archive.mpq> <folder> [filter]\n  wowcrucible mpq create <archive.mpq> <files/folders...>\n  wowcrucible mpq update <archive.mpq> <files/folders...>");
    }
}

static int Help()
{
    Console.WriteLine("WoW Crucible CLI\n\n  dbc info <file.dbc>\n  mpq list <archive.mpq> [filter]\n  mpq extract <archive.mpq> <folder> [filter]\n  mpq create <archive.mpq> <files/folders...>\n  mpq update <archive.mpq> <files/folders...>");
    return 0;
}

static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

sealed class ConsoleProgress : IProgress<(int Done, int Total, string Path)>
{
    public void Report((int Done, int Total, string Path) value) => Console.Error.WriteLine($"[{value.Done}/{value.Total}] {value.Path}");
}

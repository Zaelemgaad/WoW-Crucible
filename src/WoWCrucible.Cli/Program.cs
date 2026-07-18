using System.Diagnostics;
using WoWCrucible.Core;

var devbugRequested = args.Any(argument => argument.Equals("--devbug", StringComparison.OrdinalIgnoreCase));
var commandArguments = args.Where(argument => !argument.Equals("--devbug", StringComparison.OrdinalIgnoreCase)).ToArray();
using var devbug = CliDevbugSession.TryStart(devbugRequested, args);
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
var exitCode = 0;

try
{
    exitCode = commandArguments.Length == 0 || commandArguments[0] is "help" or "--help" or "-h" ? Help() : commandArguments[0].ToLowerInvariant() switch
    {
        "dbc" => Dbc(commandArguments[1..]),
        "db" => Database(commandArguments[1..], cancellation.Token).GetAwaiter().GetResult(),
        "server" => Server(commandArguments[1..]).GetAwaiter().GetResult(),
        "client" => Client(commandArguments[1..]),
        "asset" => Asset(commandArguments[1..]),
        "project" => Project(commandArguments[1..]),
        "mpq" => Mpq(commandArguments[1..]),
        "manifest" => Manifest(commandArguments[1..]),
        _ => Fail($"Unknown command: {commandArguments[0]}")
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    exitCode = 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    devbug?.RecordException(ex);
    exitCode = 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

devbug?.Complete(exitCode);
return exitCode;

static int Asset(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return AssetHelp();
    if (args is ["texture-info", var texturePath])
    {
        var info = BlpTextureService.Inspect(texturePath);
        Console.WriteLine($"Path\t{info.Path}\nVersion\t{info.Version}\nDimensions\t{info.Width}x{info.Height}\nEncoding\t{info.Encoding}\nAlphaDepth\t{info.AlphaDepth}\nAlphaEncoding\t{info.AlphaEncoding}\nMipmaps\t{info.MipLevels.Count} (declared={info.DeclaresMipmaps})");
        foreach (var mip in info.MipLevels) Console.WriteLine($"MIP\t{mip.Index}\t{mip.Width}x{mip.Height}\t{mip.Offset}\t{mip.Size}");
        foreach (var warning in info.Warnings) Console.WriteLine($"WARN\t{warning}");
        return 0;
    }
    if (args is ["texture-decode", var decodeSource, var decodeOutput, .. var decodeOptions])
    {
        var mipText = Option(decodeOptions, "--mip=") ?? "0";
        var overwrite = decodeOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = decodeOptions.Where(option => !option.StartsWith("--mip=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-decode option: {unknown[0]}");
        if (!int.TryParse(mipText, out var mip) || mip < 0) return Fail("--mip must be a non-negative integer.");
        BlpTextureService.DecodeToPng(decodeSource, decodeOutput, mip, overwrite);
        Console.Error.WriteLine($"Decoded native BLP mip {mip} to PNG: {Path.GetFullPath(decodeOutput)}");
        return 0;
    }
    if (args is ["texture-encode", var encodeSource, var encodeOutput, .. var encodeOptions])
    {
        var formatText = (Option(encodeOptions, "--format=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var format = formatText switch { "auto" => BlpOutputFormat.Auto, "dxt1" => BlpOutputFormat.Dxt1, "dxt1a" or "dxt1alpha" => BlpOutputFormat.Dxt1Alpha, "dxt3" => BlpOutputFormat.Dxt3, "dxt5" => BlpOutputFormat.Dxt5, _ => (BlpOutputFormat)(-1) };
        if ((int)format < 0) return Fail("--format must be auto, dxt1, dxt1a, dxt3, or dxt5.");
        var qualityText = (Option(encodeOptions, "--quality=") ?? "best").ToLowerInvariant();
        var quality = qualityText switch { "fast" => BlpOutputQuality.Fast, "balanced" => BlpOutputQuality.Balanced, "best" => BlpOutputQuality.Best, _ => (BlpOutputQuality)(-1) };
        if ((int)quality < 0) return Fail("--quality must be fast, balanced, or best.");
        var mipmaps = !encodeOptions.Contains("--no-mips", StringComparer.OrdinalIgnoreCase);
        var overwrite = encodeOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = encodeOptions.Where(option => !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-encode option: {unknown[0]}");
        BlpTextureService.EncodeFromImage(encodeSource, encodeOutput, new(format, mipmaps, quality), overwrite);
        var info = BlpTextureService.Inspect(encodeOutput);
        Console.Error.WriteLine($"Encoded {info.Width}x{info.Height} {info.Encoding} BLP2 with {info.MipLevels.Count} mip level(s): {info.Path}");
        return 0;
    }
    if (args is ["texture-validate", var validatePath, .. var validateOptions])
    {
        var recursive = validateOptions.Contains("--recursive", StringComparer.OrdinalIgnoreCase);
        var unknown = validateOptions.Where(option => !option.Equals("--recursive", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-validate option: {unknown[0]}");
        var summary = BlpTextureService.ValidateEach(validatePath, recursive, result => Console.WriteLine(result.Valid
            ? $"{(result.Info!.Warnings.Count == 0 ? "PASS" : "WARN")}\t{result.Info.Width}x{result.Info.Height}\t{result.Info.Encoding}\t{result.Info.MipLevels.Count}\t{result.Path}{(result.Info.Warnings.Count == 0 ? string.Empty : $"\t{string.Join(" | ", result.Info.Warnings)}") }"
            : $"FAIL\t{result.Error}\t{result.Path}"));
        Console.Error.WriteLine($"Validated {summary.Total:N0} BLP texture(s): {summary.Total - summary.Failures:N0} decodable, {summary.Warnings:N0} with warning(s), {summary.Failures:N0} invalid.");
        return summary.Failures == 0 && summary.Warnings == 0 ? 0 : 3;
    }
    if (args is ["library-plan", var sourceRoot, var libraryRoot, .. var planOptions])
    {
        var maxText = Option(planOptions, "--max-gb=") ?? "2";
        var unknown = planOptions.Where(option => !option.StartsWith("--max-gb=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-plan option: {unknown[0]}");
        var maximum = checked((long)(double.Parse(maxText, System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024 * 1024));
        var plan = BulkAssetLibraryService.CreatePlan(sourceRoot, libraryRoot, maximum, new Progress<(int Done, int Total, string Path)>(value => Console.Error.WriteLine($"Plan {value.Done:N0}/{value.Total:N0}\t{value.Path}")));
        Console.Error.WriteLine($"Asset library plan: {plan.Archives.Count(archive => archive.Eligible):N0} eligible archive(s), {plan.Archives.Count(archive => !archive.Eligible):N0} skipped by size, {plan.Archives.Sum(archive => archive.Entries):N0} archive entries, {plan.LooseBlpFiles + plan.Archives.Sum(archive => archive.BlpFiles):N0} BLP file(s).\nPlan: {Path.Combine(plan.LibraryRoot, "asset-library-plan.json")}");
        return plan.Archives.Any(archive => archive.Error is not null) ? 3 : 0;
    }
    if (args is ["library-run", var runLibraryRoot, .. var runOptions])
    {
        var workersText = Option(runOptions, "--workers=") ?? "6";
        var unknown = runOptions.Where(option => !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-run option: {unknown[0]}");
        var progress = new Progress<(string Stage, int Done, int Total, string Path)>(value => Console.Error.WriteLine($"{value.Stage}\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.RunAsync(runLibraryRoot, int.Parse(workersText, System.Globalization.CultureInfo.InvariantCulture), progress).GetAwaiter().GetResult();
        Console.Error.WriteLine($"Asset library complete: {result.CompletedArchives:N0} archive(s), {result.CopiedLooseBlps:N0} loose BLP copy/copies, {result.ConvertedPngs:N0} PNG conversion(s), {result.FailedArchives:N0} archive failure(s), {result.ConversionFailures:N0} conversion failure(s).\nCatalog: {result.CatalogPath}\nCheckpoint: {result.CheckpointPath}");
        return result.FailedArchives == 0 && result.ConversionFailures == 0 ? 0 : 3;
    }
    if (args is ["library-import", var extractedRoot, var importLibraryRoot, var provenance, .. var importOptions])
    {
        var workersText = Option(importOptions, "--workers=") ?? "6";
        var unknown = importOptions.Where(option => !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-import option: {unknown[0]}");
        var progress = new Progress<(string Stage, long Done, long Total, string Path)>(value =>
            Console.Error.WriteLine(value.Total > 0 ? $"{value.Stage}\t{value.Done:N0}/{value.Total:N0}\t{value.Path}" : $"{value.Stage}\t{value.Path}"));
        var result = BulkAssetLibraryService.ImportExtractedArchiveAsync(extractedRoot, importLibraryRoot, provenance,
            int.Parse(workersText, System.Globalization.CultureInfo.InvariantCulture), progress).GetAwaiter().GetResult();
        Console.Error.WriteLine($"Extracted archive import complete: {result.Provenance}, {result.SourceFiles:N0} source file(s), {result.SourceBytes / (1024d * 1024 * 1024):0.##} GiB, {result.ImportedFiles:N0} newly copied, {result.ConvertedPngs:N0} PNG conversion(s), {result.ConversionFailures:N0} conversion failure(s).\nCatalog: {result.CatalogPath}");
        return result.ConversionFailures == 0 ? 0 : 3;
    }
    if (args is ["library-repair", var repairLibraryRoot, .. var repairOptions])
    {
        var workersText = Option(repairOptions, "--workers=") ?? "6";
        var unknown = repairOptions.Where(option => !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-repair option: {unknown[0]}");
        var progress = new Progress<(string Stage, int Done, int Total, string Path)>(value => Console.Error.WriteLine($"{value.Stage}\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.RepairConversionsAsync(repairLibraryRoot, int.Parse(workersText, System.Globalization.CultureInfo.InvariantCulture), progress).GetAwaiter().GetResult();
        Console.Error.WriteLine($"Asset conversion repair complete: {result.NewlyConvertedPngs:N0} newly recovered PNG(s), {result.RemainingFailures:N0} genuinely unsupported BLP(s).\nCatalog: {result.CatalogPath}\nCheckpoint: {result.CheckpointPath}");
        return result.RemainingFailures == 0 ? 0 : 3;
    }
    if (args is ["library-artifacts", var artifactLibraryRoot, .. var artifactOptions])
    {
        var apply = artifactOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var sourceRoots = artifactOptions.Where(option => option.StartsWith("--source-root=", StringComparison.OrdinalIgnoreCase)).Select(option => option[14..]).ToArray();
        var unknown = artifactOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--source-root=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-artifacts option: {unknown[0]}");
        var progress = new ConsoleProgress(5);
        var result = BulkAssetLibraryService.RepairArchiveArtifacts(artifactLibraryRoot, apply, sourceRoots, progress);
        Console.Error.WriteLine($"Archive artifact {(result.Applied ? "repair" : "audit")}: {result.InvalidArtifacts:N0} invalid generated BLP(s), {result.Recovered:N0} {(result.Applied ? "recovered" : "recoverable")}, {result.Quarantined:N0} quarantined, {result.SourceInvalid:N0} invalid in the source archive, {result.ExtractionFailures:N0} source extraction failure(s), {result.Unmapped:N0} unmapped.\nReport: {result.ReportPath}" +
            (result.Applied ? $"\nCatalog: {result.CatalogPath}" : "\nNo processed asset changed. Review the report, then repeat with --apply."));
        return result.InvalidArtifacts == 0 || result.Applied && result.Unmapped == 0 && result.Recovered + result.Quarantined == result.InvalidArtifacts ? 0 : 3;
    }
    if (args is ["library-layout", var layoutLibraryRoot, .. var layoutOptions])
    {
        var apply = layoutOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = layoutOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-layout option: {unknown[0]}");
        var progress = new Progress<(long Done, long Total, string Path)>(value => Console.Error.WriteLine($"Layout\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.MigrateToContentFirstLayout(layoutLibraryRoot, apply, progress);
        Console.Error.WriteLine($"Content-first layout {(result.Applied ? "migration" : "dry run")}: {result.SourceFolders:N0} provenance folder(s), {result.Files:N0} file(s), {result.Bytes / (1024d * 1024 * 1024):0.##} GiB, {result.MovedFiles:N0} moved, {result.Conflicts:N0} conflict(s).{(result.Applied ? $"\nCatalog: {result.CatalogPath}" : "\nRun again with --apply after reviewing this result.")}");
        return result.Conflicts == 0 ? 0 : 3;
    }
    if (args is ["library-consolidate", var consolidateLibraryRoot, .. var consolidateOptions])
    {
        var apply = consolidateOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = consolidateOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-consolidate option: {unknown[0]}");
        var progress = new Progress<(long Done, long Total, string Path)>(value => Console.Error.WriteLine($"Consolidate\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.ConsolidateLooseLayout(consolidateLibraryRoot, apply, progress);
        var catalogStatus = result.CatalogRebuildError is null
            ? result.Applied ? $"\nCatalog: {result.CatalogPath}" : string.Empty
            : $"\nCATALOG REBUILD FAILED after file consolidation committed: {result.CatalogRebuildError}\nRecover with: wowcrucible asset library-catalog \"{Path.GetFullPath(consolidateLibraryRoot)}\"";
        Console.Error.WriteLine($"Loose consolidation {(result.Applied ? "applied" : "dry run")}: {result.Files:N0} file(s), {result.Bytes / (1024d * 1024 * 1024):0.##} GiB, {result.MovedFiles:N0} move(s), {result.ExactDuplicates:N0} byte-identical duplicate(s), {result.Conflicts:N0} non-identical conflict(s).{(result.Applied ? $"\nJournal: {result.JournalPath}{catalogStatus}" : result.Conflicts == 0 ? "\nNo files changed. Run again with --apply after reviewing this result." : "\nNo files changed. Resolve every conflict before applying.")}");
        return result.Conflicts == 0 && result.CatalogRebuildError is null ? 0 : 3;
    }
    if (args is ["library-catalog", var catalogLibraryRoot])
    {
        var catalogPath = BulkAssetLibraryService.RebuildCatalog(catalogLibraryRoot);
        Console.Error.WriteLine($"Asset catalog rebuilt successfully: {catalogPath}");
        return 0;
    }
    if (args is ["library-status", var statusLibraryRoot])
    {
        var plan = BulkAssetLibraryService.LoadPlan(statusLibraryRoot);
        var checkpointPath = Path.Combine(Path.GetFullPath(statusLibraryRoot), "asset-library-checkpoint.json");
        var checkpoint = File.Exists(checkpointPath) ? System.Text.Json.JsonSerializer.Deserialize<BulkAssetLibraryCheckpoint>(File.ReadAllText(checkpointPath)) : null;
        Console.WriteLine($"Source\t{plan.SourceRoot}\nLibrary\t{plan.LibraryRoot}\nEligibleArchives\t{plan.Archives.Count(archive => archive.Eligible && archive.Error is null)}\nCompletedArchives\t{checkpoint?.CompletedArchiveIds.Count ?? 0}\nSkippedArchives\t{plan.Archives.Count(archive => !archive.Eligible)}\nArchiveEntries\t{plan.Archives.Sum(archive => archive.Entries)}\nBLPs\t{plan.LooseBlpFiles + plan.Archives.Sum(archive => archive.BlpFiles)}\nConvertedPNGs\t{checkpoint?.ConvertedPngFiles ?? 0}\nEntryOrArchiveFailures\t{checkpoint?.Failures.Count ?? 0}\nCheckpoint\t{(File.Exists(checkpointPath) ? checkpointPath : "not started")}");
        if (checkpoint is not null) foreach (var failure in checkpoint.Failures) Console.WriteLine($"FAILURE\t{failure.Key}\t{failure.Value}");
        return checkpoint?.Failures.Count > 0 ? 3 : 0;
    }
    if (args is ["compare-folders", var comparisonLibrary, .. var comparisonFilter])
    {
        var query = string.Join(' ', comparisonFilter); var index = AssetComparisonService.BuildIndex(comparisonLibrary);
        foreach (var directory in index.Directories.Where(directory => query.Length == 0 || directory.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{directory.PngFiles}\t{directory.ProvenanceSources}\t{directory.LogicalPath}");
        Console.Error.WriteLine($"Indexed {index.TotalPngFiles:N0} PNGs in {index.Directories.Count:N0} content directories. Results are grouped by directory, never by filename."); return 0;
    }
    if (args is ["compare-files", var fileComparisonLibrary, var logicalDirectory])
    {
        var index = AssetComparisonService.BuildIndex(fileComparisonLibrary); var entries = AssetComparisonService.GetDirectoryPngs(index, logicalDirectory);
        foreach (var entry in entries) Console.WriteLine($"{entry.Provenance}\t{entry.FileName}\t{entry.Bytes}\t{entry.FullPath}");
        Console.Error.WriteLine($"Found {entries.Count:N0} direct PNG(s) from {entries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} source(s) in '{logicalDirectory}'."); return 0;
    }
    if (args is ["models", var modelLibrary, var modelLogicalDirectory])
    {
        var index = AssetComparisonService.BuildIndex(modelLibrary); var discovery = AssetComparisonService.GetRelevantModels(index, modelLogicalDirectory);
        foreach (var model in discovery.Models) Console.WriteLine($"{model.Compatibility}\t{model.Version?.ToString() ?? "-"}\t{model.Provenance}\t{model.LogicalPath}\t{model.FileName}\t{model.SkinPath ?? "-"}\t{model.Status}");
        Console.Error.WriteLine($"Discovered {discovery.Models.Count:N0} M2 model(s), {discovery.Models.Count(model => model.Compatibility == AssetModelCompatibility.Ready):N0} ready, using nearest content scope '{discovery.DiscoveryScope}'."); return discovery.Models.Any(model => model.Compatibility == AssetModelCompatibility.Ready) ? 0 : 3;
    }
    if (args is ["definitive-status", var projectLibrary])
    {
        var projectPath = DefinitiveAssetProjectService.DefaultPath(projectLibrary); var project = DefinitiveAssetProjectService.LoadOrCreate(projectPath, projectLibrary);
        foreach (var group in project.Entries.GroupBy(entry => entry.GroupId)) { var first = group.First(); Console.WriteLine($"{first.Decision}\t{first.Category}\t{group.Count()}\t{first.Provenance}\t{first.ArchivePath}\t{first.Notes}"); }
        Console.Error.WriteLine($"Definitive Set: {project.Entries.Count:N0} file record(s) across {project.Entries.Select(entry => entry.GroupId).Distinct().Count():N0} decision group(s).\nProject: {projectPath}"); return 0;
    }
    if (args is ["definitive-stage", var stageLibrary, var definitiveOutput])
    {
        var projectPath = DefinitiveAssetProjectService.DefaultPath(stageLibrary); var project = DefinitiveAssetProjectService.LoadOrCreate(projectPath, stageLibrary); var result = DefinitiveAssetProjectService.StageKeepers(projectPath, project, definitiveOutput);
        Console.Error.WriteLine($"Staged {result.Files:N0} keeper file(s), {result.Bytes:N0} bytes.\nManifest: {result.ManifestPath}"); return 0;
    }
    if (args is ["inspect", .. var inspectInputs] && inspectInputs.Length > 0)
    {
        foreach (var input in inspectInputs)
        {
            var inspection = NativeAssetConversionService.Inspect(input);
            Console.WriteLine($"{inspection.Compatibility}\t{inspection.Format}\t{inspection.Magic}\t{inspection.Version?.ToString() ?? "-"}\t{inspection.Size}\t{inspection.Path}");
            foreach (var finding in inspection.Findings) Console.WriteLine($"  {finding}");
            foreach (var dependency in inspection.Dependencies) Console.WriteLine($"  dependency\t{dependency.Kind}\t{dependency.Path}\t{dependency.Sha256}");
        }
        return 0;
    }
    if (args is ["preview-info", var previewModelPath, .. var previewOptions])
    {
        var known = previewOptions.Where(option => option.Equals("--all-geosets", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--hair=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--facial-hair=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (known.Length != previewOptions.Length) return Fail($"Unknown preview-info option: {previewOptions.Except(known).First()}");
        var mode = previewOptions.Contains("--all-geosets", StringComparer.OrdinalIgnoreCase) ? M2PreviewVisibilityMode.AllGeosets : M2PreviewVisibilityMode.BaseAppearance;
        var dbcFolder = Option(previewOptions, "--dbc="); var hairText = Option(previewOptions, "--hair="); var facialText = Option(previewOptions, "--facial-hair=");
        M2GeosetSelection? selection = null;
        if (hairText is not null || facialText is not null)
        {
            if (dbcFolder is null) return Fail("--hair and --facial-hair require --dbc=<folder> so Crucible can resolve exact build-12340 geosets.");
            var identity = CharacterAppearanceService.Infer(Path.GetDirectoryName(Path.GetFullPath(previewModelPath)) ?? string.Empty, Path.GetFileName(previewModelPath))
                ?? throw new InvalidDataException("The model path/name does not identify a supported playable race and sex.");
            var plan = CharacterAppearanceService.ResolveGeosets(dbcFolder, identity, ParseVariation(hairText), ParseVariation(facialText));
            selection = plan.GroupVariants.Count == 0 ? null : new(plan.GroupVariants, "CharHairGeosets.dbc + CharacterFacialHairStyles.dbc");
            foreach (var warning in plan.Warnings) Console.Error.WriteLine($"WARNING: {warning}");
        }
        var geometry = M2PreviewGeometryService.Load(previewModelPath, visibilityMode: mode, geosetSelection: selection);
        Console.WriteLine($"Model\t{geometry.ModelPath}\nSkin\t{geometry.SkinPath}\nVertices\t{geometry.Vertices.Count:N0}\nGeosets\t{geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} ({geometry.VisibilityMode})\nTriangles\t{geometry.TriangleIndices.Count / 3:N0}/{geometry.TotalTriangleIndices / 3:N0}\nMinimum\t{geometry.Minimum}\nMaximum\t{geometry.Maximum}");
        if (geometry.GeosetSelection is not null) Console.WriteLine($"GEOSET_SELECTION\t{geometry.GeosetSelection.Source}\t{string.Join(",", geometry.GeosetSelection.GroupVariants.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}");
        foreach (var slot in geometry.TextureSlots) Console.WriteLine($"TEXTURE\t{slot.Index}\t{slot.Type}\t{slot.Flags}\t{slot.EmbeddedPath ?? "<external appearance binding>"}");
        foreach (var material in geometry.MaterialUnits) Console.WriteLine($"MATERIAL\t{material.Index}\tsubmesh={material.SubmeshIndex}\tshader={material.ShaderId}\tlookup={material.TextureLookupIndex}\ttexture={(material.TextureDefinitionIndex < 0 ? "<unresolved>" : material.TextureDefinitionIndex)}\tpasses={material.TextureCount}");
        foreach (var batch in geometry.Batches) Console.WriteLine($"BATCH\t{submeshLabel(batch)}\tindices={batch.TriangleStart}+{batch.TriangleIndexCount}\tmaterial={batch.MaterialUnitIndex?.ToString() ?? "<none>"}\ttexture={batch.TextureDefinitionIndex?.ToString() ?? "<none>"}");
        return 0;

        static string submeshLabel(M2PreviewBatch batch) => $"submesh={batch.SubmeshIndex},geoset={batch.GeosetId}";
        static uint? ParseVariation(string? value) => value is null ? null : uint.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"Invalid non-negative appearance variation: {value}");
    }
    if (args is ["appearance-info", var charSectionsPath, var logicalPath, var modelFile])
    {
        var identity = CharacterAppearanceService.Infer(logicalPath, modelFile) ?? throw new InvalidDataException("The logical path/model name does not identify a supported playable race and sex.");
        var skins = CharacterAppearanceService.LoadBaseSkins(charSectionsPath, identity);
        Console.WriteLine($"Character\t{identity.RaceName}\t{identity.SexName}\tRaceID={identity.RaceId}\tSexID={identity.SexId}\nBaseSkins\t{skins.Count:N0}");
        foreach (var skin in skins) Console.WriteLine($"SKIN\t{skin.Id}\tvariation={skin.VariationIndex}\tcolor={skin.ColorIndex}\tflags=0x{skin.Flags:X}\t{skin.TexturePath}");
        return 0;
    }
    if (args is ["appearance-compose", var baseTexturePath, var outputPng, .. var composeOptions])
    {
        var knownPrefixes = new[] { "--torso=", "--pelvis=", "--face-upper=", "--face-lower=", "--facial-upper=", "--facial-lower=", "--scalp-upper=", "--scalp-lower=" };
        var unknown = composeOptions.Where(option => !knownPrefixes.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) throw new ArgumentException($"Unknown appearance-compose option(s): {string.Join(", ", unknown)}");
        var layers = new List<CharacterTextureLayer>();
        Add("--torso=", CharacterTextureRegion.Torso); Add("--pelvis=", CharacterTextureRegion.Pelvis); Add("--face-upper=", CharacterTextureRegion.FaceUpper); Add("--face-lower=", CharacterTextureRegion.FaceLower);
        Add("--facial-upper=", CharacterTextureRegion.FaceUpper); Add("--facial-lower=", CharacterTextureRegion.FaceLower); Add("--scalp-upper=", CharacterTextureRegion.FaceUpper); Add("--scalp-lower=", CharacterTextureRegion.FaceLower);
        var composed = CharacterTextureComposer.Compose(BlpTextureService.Decode(baseTexturePath), layers);
        BlpTextureService.WritePng(outputPng, composed, composeOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase));
        Console.WriteLine($"Composed\t{composed.Width}x{composed.Height}\t{layers.Count:N0} layer(s)\t{Path.GetFullPath(outputPng)}");
        return 0;
        void Add(string prefix, CharacterTextureRegion region) { var path = Option(composeOptions, prefix); if (path is not null) layers.Add(new(BlpTextureService.Decode(path), region)); }
    }
    if (args is ["workspace", var outputRoot, .. var workspaceInputs] && workspaceInputs.Length > 0)
    {
        var workspace = NativeAssetConversionService.CreateWorkspace(workspaceInputs, outputRoot);
        Console.Error.WriteLine($"Created native conversion workspace: {workspace.RootPath}\nAlready compatible: {workspace.CompatibleAssets:N0}\nRequire conversion: {workspace.ConversionRequired:N0}\nBlocked/invalid: {workspace.BlockedAssets:N0}\nReport: {Path.Combine(workspace.RootPath, "conversion-report.json")}");
        return workspace.BlockedAssets == 0 ? 0 : 3;
    }
    return AssetHelp(2);
}

static int AssetHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible asset texture-info <file.blp>\n  wowcrucible asset texture-decode <file.blp> <output.png> [--mip=N] [--overwrite]\n  wowcrucible asset texture-encode <image.png|jpg|bmp|tga> <output.blp> [--format=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--overwrite]\n  wowcrucible asset texture-validate <file-or-folder> [--recursive]\n  wowcrucible asset inspect <model.m2|building.wmo>...\n  wowcrucible asset preview-info <wrath-model.m2> [--dbc=folder] [--hair=N] [--facial-hair=N] [--all-geosets]\n  wowcrucible asset appearance-info <CharSections.dbc> <logical-path> <model-file>\n  wowcrucible asset appearance-compose <base.blp> <output.png> [component options] [--overwrite]\n  wowcrucible asset models <library-folder> <logical-directory>\n  wowcrucible asset definitive-status <library-folder>\n  wowcrucible asset definitive-stage <library-folder> <output-folder>\n  wowcrucible asset workspace <new-output-folder> <files/folders...>\n  wowcrucible asset library-plan <source-folder> <library-folder> [--max-gb=2]\n  wowcrucible asset library-run <library-folder> [--workers=6]\n  wowcrucible asset library-import <extracted-folder> <library-folder> <provenance> [--workers=6]\n  wowcrucible asset library-repair <library-folder> [--workers=6]\n  wowcrucible asset library-artifacts <library-folder> [--source-root=folder]... [--apply]\n  wowcrucible asset library-layout <library-folder> [--apply]\n  wowcrucible asset library-consolidate <library-folder> [--apply]\n  wowcrucible asset library-catalog <library-folder>\n  wowcrucible asset library-status <library-folder>\n  wowcrucible asset compare-folders <library-folder> [path-filter]\n  wowcrucible asset compare-files <library-folder> <logical-directory>\n\nFull guide: docs/CLI-REFERENCE.md", code);

static int Project(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ProjectHelp();
    if (args is ["create", var root, var name, .. var createOptions])
    {
        var target = Option(createOptions, "--target=") ?? TargetProfileCatalog.DefaultProfileId; var library = Option(createOptions, "--asset-library=");
        var unknown = createOptions.Where(option => !option.StartsWith("--target=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--asset-library=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown project create option: {unknown[0]}");
        var project = CrucibleContentProjectService.Create(root, name, target, library); Console.Error.WriteLine($"Created {project.Name} at {Path.GetFullPath(root)}\nTarget: {project.TargetProfile}\nID registry: {Path.Combine(Path.GetFullPath(root), project.IdRegistryFile)}"); return 0;
    }
    if (args is ["status", var projectRoot])
    {
        var project = CrucibleContentProjectService.Load(projectRoot); var registry = CrucibleContentProjectService.LoadRegistry(projectRoot);
        Console.WriteLine($"Name\t{project.Name}\nTarget\t{project.TargetProfile}\nAssetLibrary\t{project.AssetLibrary ?? "not linked"}\nReservations\t{registry.Reservations.Count}\nReservedIDs\t{registry.Reservations.Sum(reservation => reservation.Values.Count)}");
        foreach (var group in registry.Reservations.GroupBy(reservation => reservation.Domain)) Console.WriteLine($"DOMAIN\t{group.Key}\t{group.Sum(reservation => reservation.Values.Count)}"); return 0;
    }
    if (args is ["reserve-ids", var reserveRoot, var domainText, var countText, .. var reserveOptions] && Enum.TryParse<ContentIdDomain>(domainText, true, out var domain) && int.TryParse(countText, out var count))
    {
        var startText = Option(reserveOptions, "--start=") ?? "100000"; var occupiedPath = Option(reserveOptions, "--occupied="); var purpose = Option(reserveOptions, "--purpose=") ?? "Unspecified content";
        var unknown = reserveOptions.Where(option => !option.StartsWith("--start=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--occupied=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--purpose=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown ID reservation option: {unknown[0]}");
        if (!uint.TryParse(startText, out var start)) return Fail("--start must be an unsigned integer."); IReadOnlyList<uint> occupied = occupiedPath is null ? [] : CrucibleContentProjectService.ReadOccupiedIds(occupiedPath);
        var result = CrucibleContentProjectService.ReserveIds(reserveRoot, domain, count, start, occupied, purpose); Console.WriteLine(string.Join(Environment.NewLine, result.Reservation.Values));
        Console.Error.WriteLine($"Reserved {result.Reservation.Values.Count:N0} {domain} ID(s), {result.Reservation.Values.First():N0}–{result.Reservation.Values.Last():N0}, for {result.Reservation.Purpose}.{(occupiedPath is null ? " WARNING: no live DBC/SQL occupied-ID list was supplied." : $" Checked occupied IDs from {Path.GetFullPath(occupiedPath)}.")}"); return occupiedPath is null ? 3 : 0;
    }
    return ProjectHelp(2);
}

static int ProjectHelp(int code = 0) => GroupHelp($"Usage:\n  wowcrucible project create <folder> <name> [--target={TargetProfileCatalog.DefaultProfileId}] [--asset-library=folder]\n  wowcrucible project status <project-folder>\n  wowcrucible project reserve-ids <project-folder> <domain> <count> [--start=N] [--occupied=ids.txt] [--purpose=text]\n\nID domains: Item, ItemSet, Spell, CreatureTemplate, CreatureModelData, CreatureDisplayInfo, CreatureDisplayInfoExtra, GameObject, Race, Class, Faction, Mount, Quest, Custom", code);

static int Client(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ClientHelp();
    if (args is ["install-patch", var sourcePatch, var installClientRoot, .. var installOptions])
    {
        var targetName = Option(installOptions, "--name=");
        var unknown = installOptions.Where(option => !option.StartsWith("--name=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client patch install option: {unknown[0]}");
        var result = ClientPatchDeploymentService.Install(sourcePatch, installClientRoot, targetName);
        Console.Error.WriteLine($"Installed {result.InstalledPath}\nSHA-256 {result.Sha256}\nBackup: {result.BackupPath ?? "not needed"}\nCache: {(result.Cache.Existed ? $"deleted {result.Cache.DeletedFiles:N0} file(s), {result.Cache.DeletedBytes:N0} bytes" : "already absent")}");
        return 0;
    }
    if (args is ["clear-cache", var cacheClientRoot])
    {
        var result = ClientPatchDeploymentService.InvalidateCache(cacheClientRoot);
        Console.Error.WriteLine(result.Existed
            ? $"Deleted {result.CachePath} ({result.DeletedFiles:N0} file(s), {result.DeletedBytes:N0} bytes)."
            : $"Client cache is already absent: {result.CachePath}");
        return 0;
    }
    if (args is ["fusion", var baseRoot, .. var fusionInputs])
    {
        var stage = Option(fusionInputs, "--stage="); var output = Option(fusionInputs, "--output="); var showAll = fusionInputs.Contains("--all", StringComparer.OrdinalIgnoreCase);
        var sourcePaths = fusionInputs.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var unknown = fusionInputs.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.StartsWith("--stage=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !value.Equals("--all", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client fusion option: {unknown[0]}");
        if (sourcePaths.Length == 0) return Fail("Client fusion requires at least one extracted/effective override source.");
        var sources = sourcePaths.Select((path, index) => new ClientFusionSource($"source-{index + 1}-{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}", path)).ToArray();
        var plan = ClientFusionPlanner.Analyze(baseRoot, sources, new ConsoleProgress(5));
        foreach (var entry in plan.Entries.Where(entry => showAll && entry.Status != ClientFusionStatus.IdenticalToBase || entry.Status == ClientFusionStatus.Conflict))
            Console.WriteLine($"{entry.Status}\t{entry.ArchivePath}\t{entry.Candidates.Count}\t{entry.Guidance}");
        var conflicts = plan.Entries.Count(entry => entry.Status == ClientFusionStatus.Conflict);
        Console.Error.WriteLine($"Fusion plan: {plan.Entries.Count(entry => entry.Status != ClientFusionStatus.IdenticalToBase):N0} changed path(s), {conflicts:N0} unresolved conflict(s), {plan.Entries.Count(entry => entry.Status == ClientFusionStatus.IdenticalToBase):N0} base-identical omission(s).");
        if (output is not null) { ClientFusionPlanner.Save(output, plan); Console.Error.WriteLine($"Saved fusion plan: {Path.GetFullPath(output)}"); }
        if (stage is not null)
        {
            var result = ClientFusionPlanner.Stage(stage, plan);
            Console.Error.WriteLine($"Staged {result.StagedFiles:N0} resolved path(s); excluded {result.UnresolvedConflicts:N0} conflict(s). Manifest: {result.ManifestPath}");
        }
        return conflicts == 0 ? 0 : 3;
    }
    if (args is ["index", var clientRoot, var outputDirectory, .. var options])
    {
        var unknown = options.Where(option => !option.Equals("--no-hash", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client index option: {unknown[0]}");
        var progress = new ClientIndexConsoleProgress();
        var listFile = Option(options, "--listfile=");
        var clientExecutable = Option(options, "--client-exe=");
        var index = new ClientArchiveIndexService().Build(clientRoot, outputDirectory, !options.Contains("--no-hash", StringComparer.OrdinalIgnoreCase), progress, externalListFile: listFile, executablePath: clientExecutable);
        Console.Error.WriteLine($"Indexed {index.Archives.Count:N0} MPQs for {index.Name}: {index.Archives.Sum(archive => archive.PayloadFiles):N0} payload paths, {index.Archives.Sum(archive => archive.AnonymousFiles):N0} unresolved name(s), {index.Archives.Count(archive => archive.Error is not null):N0} archive error(s). Index: {Path.GetFullPath(outputDirectory)}");
        return index.Archives.Any(archive => archive.Error is not null) ? 1 : 0;
    }
    if (args is ["corpus", var outputFile, .. var indexDirectories] && indexDirectories.Length > 0)
    {
        var count = ClientArchiveIndexService.CreatePathCorpus(indexDirectories, outputFile);
        Console.Error.WriteLine($"Wrote {count:N0} distinct known MPQ paths to {Path.GetFullPath(outputFile)}");
        return 0;
    }
    if (args is ["extract", var extractIndexDirectory, var archiveRelativePath, var destination, .. var extractOptions])
    {
        var quiet = extractOptions.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        var resolvedOnly = extractOptions.Contains("--resolved-only", StringComparer.OrdinalIgnoreCase);
        var anonymousOnly = extractOptions.Contains("--anonymous-only", StringComparer.OrdinalIgnoreCase);
        var overwrite = extractOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = extractOptions.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.Equals("--resolved-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--anonymous-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client extract option: {unknown[0]}");
        var filters = extractOptions.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (filters.Length > 1) return Fail("Client extract accepts at most one path filter.");
        var started = Stopwatch.StartNew();
        var result = ClientArchiveIndexService.ExtractIndexed(extractIndexDirectory, archiveRelativePath, destination, filters.FirstOrDefault(), resolvedOnly, anonymousOnly, overwrite, quiet ? null : new ConsoleProgress(100));
        Console.Error.WriteLine($"Selected {result.SelectedFiles:N0} indexed file(s); extracted {result.ExtractedFiles:N0}, resumed past {result.SkippedExistingFiles:N0} existing file(s) in {started.Elapsed.TotalSeconds:0.00}s. Destination: {Path.GetFullPath(destination)}");
        return 0;
    }
    if (args is ["show", var showIndexDirectory])
    {
        var index = ClientArchiveIndexService.Load(showIndexDirectory);
        var loose = index.LooseFiles ?? [];
        Console.WriteLine($"Client\t{index.Name}\nRoot\t{index.ClientRoot}\nComplete\t{index.Complete}\nArchives\t{index.CompletedArchives}\nActiveLocale\t{index.ActiveLocale ?? "unknown"}\nExecutablePath\t{index.Executable?.Path ?? "missing"}\nExecutable\t{index.Executable?.FileVersion ?? "missing"}\nExecutableSha256\t{index.Executable?.Sha256 ?? "missing"}\nPayloadPaths\t{index.Archives.Sum(archive => archive.PayloadFiles)}\nAnonymousPaths\t{index.Archives.Sum(archive => archive.AnonymousFiles)}\nBackupArchives\t{index.Archives.Count(archive => archive.Scope == ClientArchiveScope.Backup)}\nInactiveLocaleArchives\t{index.Archives.Count(archive => archive.Scope == ClientArchiveScope.InactiveLocale)}\nCustomSubdirectoryArchives\t{index.Archives.Count(archive => archive.Scope == ClientArchiveScope.CustomSubdirectory)}\nLooseFiles\t{loose.Count}\nRuntimeFiles\t{loose.Count(file => file.Scope == ClientLooseFileScope.Runtime)}\nAddOnFiles\t{loose.Count(file => file.Scope == ClientLooseFileScope.AddOn)}\nArchiveErrors\t{index.Archives.Count(archive => archive.Error is not null)}");
        return 0;
    }
    return ClientHelp(2);
}

static int ClientHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible client install-patch <patch.mpq> <client-root> [--name=patch-X.MPQ]\n  wowcrucible client clear-cache <client-root>\n  wowcrucible client index <client-root> <index-directory> [--no-hash] [--listfile=paths.txt] [--client-exe=Wow.exe]\n  wowcrucible client corpus <output-listfile> <index-directory>...\n  wowcrucible client extract <index-directory> <archive-relative-path> <folder> [path-glob-or-text] [--resolved-only|--anonymous-only] [--overwrite] [--quiet]\n  wowcrucible client show <index-directory>\n  wowcrucible client fusion <base-root> <override-root>... [--output=plan.json] [--stage=review-folder] [--all]", code);

static async Task<int> Server(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ServerHelp();
    if (args is ["client-plan", var planServerFolder, var clientDbcRoot, .. var planOptions])
    {
        var source = Option(planOptions, "--source="); var output = Option(planOptions, "--output="); var stage = Option(planOptions, "--stage=");
        var unknown = planOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--stage=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown server client-plan option: {unknown[0]}");
        var planWorkspace = await ServerWorkspaceDetector.DetectAsync(planServerFolder);
        var plan = ClientServerDeploymentPlanner.Analyze(clientDbcRoot, planWorkspace, source);
        foreach (var entry in plan.Entries.Where(entry => entry.Status != ClientServerPlanStatus.Identical))
            Console.WriteLine($"{entry.Status}\t{entry.DbcFileName}\t{entry.Consumption}\t{entry.SqlTableName ?? "-"}\t{entry.Guidance}");
        if (output is not null) { ClientServerDeploymentPlanner.Save(output, plan); Console.Error.WriteLine($"Saved deployment plan: {Path.GetFullPath(output)}"); }
        if (stage is not null)
        {
            var result = ClientServerDeploymentPlanner.Stage(stage, plan);
            Console.Error.WriteLine($"Staged {result.ClientFiles:N0} client and {result.ServerFiles:N0} server DBC file(s); {result.BlockedFiles:N0} unresolved. Plan: {result.PlanPath}");
        }
        var blocked = plan.Entries.Count(entry => entry.Status is ClientServerPlanStatus.ConflictingClientLayers or ClientServerPlanStatus.InvalidDbc or ClientServerPlanStatus.UnknownConsumer or ClientServerPlanStatus.MissingServerDbc);
        Console.Error.WriteLine($"Client-to-server plan: {plan.Entries.Count:N0} table(s), {blocked:N0} blocked/unresolved.");
        return blocked == 0 ? 0 : 3;
    }
    if (args is ["bindings", var bindingFolder, .. var bindingOptions])
    {
        var source = Option(bindingOptions, "--source=");
        var unknown = bindingOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown server bindings option: {unknown[0]}");
        var bindingWorkspace = await ServerWorkspaceDetector.DetectAsync(bindingFolder);
        foreach (var binding in ServerTableBindingCatalog.Resolve(bindingWorkspace.CoreFamily, source))
            Console.WriteLine($"{binding.Consumption}\t{binding.DbcFileName}\t{binding.SqlTableName ?? "-"}\t{binding.KeyStrategy.Kind}\t{binding.Restart}\t{binding.Profile}\t{binding.SupportedRevision}");
        return 0;
    }
    if (args is ["dbc-audit", var auditFolder, var dbcInput, var schemaPath, .. var auditOptions])
    {
        var source = Option(auditOptions, "--source="); var migration = Option(auditOptions, "--migration=");
        var showAll = auditOptions.Any(option => option.Equals("--all", StringComparison.OrdinalIgnoreCase)); var summaryOnly = auditOptions.Any(option => option.Equals("--summary", StringComparison.OrdinalIgnoreCase));
        var unknown = auditOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--migration=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--all", StringComparison.OrdinalIgnoreCase) && !option.Equals("--summary", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown server dbc-audit option: {unknown[0]}");
        var auditWorkspace = await ServerWorkspaceDetector.DetectAsync(auditFolder);
        var dbcPath = File.Exists(dbcInput) ? Path.GetFullPath(dbcInput) : Path.Combine(auditWorkspace.DbcPath, Path.GetFileName(dbcInput));
        if (!File.Exists(dbcPath)) throw new FileNotFoundException("The DBC was not found in the detected server data folder.", dbcPath);
        var dbc = WdbcFile.Load(dbcPath); var resolution = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(Path.GetFileNameWithoutExtension(dbcPath), dbc.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(Path.GetFileNameWithoutExtension(dbcPath), dbc.FieldCount, resolution));
        var binding = ServerTableBindingCatalog.ApplySchemaKey(ServerTableBindingCatalog.ResolveFile(auditWorkspace.CoreFamily, dbcPath, source), resolution);
        PrintBinding(binding);
        if (binding.Consumption != ServerTableConsumption.SqlOverlayed || binding.SqlTableName is null) return binding.Consumption == ServerTableConsumption.Unknown ? 1 : 0;
        var capabilities = await new DatabaseCapabilityService().InspectAsync(auditWorkspace.WorldDatabase);
        var table = capabilities.FindTable(binding.SqlTableName) ?? throw new InvalidDataException($"The live world database has no expected overlay table {binding.SqlTableName}.");
        var audit = await new DbcSqlAuditService().AuditAsync(auditWorkspace.WorldDatabase, binding, dbcPath, resolution, table);
        if (!summaryOnly)
            foreach (var row in audit.Rows.Where(row => showAll || row.Status != DbcSqlRowStatus.Same))
                Console.WriteLine($"{row.Status}\t{row.Key}\t{row.Dimensions}\tDBC {FormatValues(row.DbcValues)}\tSQL {FormatValues(row.SqlValues)}");
        Console.Error.WriteLine($"Audited {audit.Rows.Count:N0} effective rows: {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.Same):N0} same, {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.SqlOverridesDbc):N0} SQL override(s), {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.DbcOnly):N0} DBC-only, {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.MissingDbcRow):N0} SQL-only/missing DBC. SQL overrides only rows that actually exist in the overlay.");
        if (migration is not null)
        {
            File.WriteAllText(migration, DbcSqlAuditService.CreateIdempotentMigration(audit));
            Console.Error.WriteLine($"Wrote idempotent DBC-to-SQL migration preview: {Path.GetFullPath(migration)}");
        }
        return audit.MismatchCount == 0 ? 0 : 3;
    }
    if (args is not [var action, var folder] || action is not ("detect" or "inspect")) return ServerHelp(2);
    var workspace = await ServerWorkspaceDetector.DetectAsync(folder);
    Console.WriteLine($"Root\t{workspace.RootPath}"); Console.WriteLine($"Core\t{workspace.CoreFamily}");
    Console.WriteLine($"Config\t{workspace.ConfigLocation}"); Console.WriteLine($"DBC\t{workspace.DbcPath}");
    Console.WriteLine($"WorldDatabase\t{workspace.WorldDatabase.Database}"); Console.WriteLine($"DatabaseEndpoint\t{workspace.WorldDatabase.Host}:{workspace.WorldDatabase.Port}");
    Console.WriteLine($"DatabaseUser\t{workspace.WorldDatabase.User}"); Console.WriteLine($"Layout\t{(workspace.UsesWsl ? "WSL split" : "Native/local")}");
    if (action == "inspect")
    {
        var capabilities = await new DatabaseCapabilityService().InspectAsync(workspace.WorldDatabase);
        Console.WriteLine($"DatabaseServer\t{capabilities.ServerVersion}");
        foreach (var table in capabilities.Tables.Values.OrderBy(table => table.Name)) Console.WriteLine($"TABLE\t{table.Name}\t{table.Columns.Count} columns");
        foreach (var inspected in ServerTableBindingCatalog.AttachCapabilities(ServerTableBindingCatalog.BuiltIn(workspace.CoreFamily), capabilities).Where(item => item.Binding.Consumption == ServerTableConsumption.SqlOverlayed))
            Console.WriteLine($"DBC_BINDING\t{inspected.Binding.DbcFileName}\t{inspected.Binding.SqlTableName}\t{(inspected.ExpectedSqlTablePresent ? "ready" : "MISSING SQL TABLE")}");
        Console.Error.WriteLine($"Found {capabilities.DbcOverlayTables.Count:N0} live DBC SQL overlay table(s).");
    }
    return 0;
}

static int ServerHelp(int code = 0)
{
    var text = "Usage:\n  wowcrucible server detect <installed-server-folder>\n  wowcrucible server inspect <installed-server-folder>\n  wowcrucible server bindings <installed-server-folder> [--source=core-source]\n  wowcrucible server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all|--summary] [--migration=output.sql]\n  wowcrucible server client-plan <installed-server-folder> <extracted-dbc-root> [--source=core-source] [--output=plan.json] [--stage=review-folder]";
    if (code == 0) Console.WriteLine(text); else Console.Error.WriteLine(text); return code;
}

static async Task<int> Database(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return DatabaseHelp();
    if (args[0].Equals("draft-template", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 3) return Fail("db draft-template requires a supported authoring domain and an output JSON path."); var options = args[3..]; var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown draft-template option: {unknown[0]}");
        object draft = args[1].ToLowerInvariant() switch
        {
            "gameobject" or "go" => new GameObjectTemplateDraft(900000, 3, 0, "New Crucible Gameobject", "", "", "", 1, new long[24], "", ""),
            "creature" => new CreatureTemplateDraft(900000, "New Crucible Creature", "", [], 80, 80, 35, 0, 0, 7, 0, 1, 1, 1, 1.14286f, 1, 1, 1, 1, 2000, 2000, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", ""),
            "quest" => new QuestPortableDraft(QuestTemplateAdapter.CreateDefaultValues(QuestTemplateAdapter.CreatePortableTable()).ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase), new()),
            _ => CreateBehaviorDraft(args[1])
        };
        var path = Path.GetFullPath(args[2]); if (File.Exists(path) && !overwrite) return Fail($"Output already exists: {path}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(draft, draft.GetType(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken); Console.Error.WriteLine($"Draft template: {path}"); return 0;
    }
    if (args[0].Equals("recovery-audit", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 3) return DatabaseHelp(2);
        var auditOptions = args[3..];
        var baselineOptions = auditOptions.Where(option => option.StartsWith("--baseline=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (baselineOptions.Length > 1) return Fail("Specify --baseline only once.");
        var baseline = baselineOptions.Length == 0 ? null : baselineOptions[0][11..];
        if (baselineOptions.Length == 1 && string.IsNullOrWhiteSpace(baseline)) return Fail("--baseline requires a non-empty snapshot path.");
        var includes = auditOptions.Where(option => option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var excludes = auditOptions.Where(option => option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var includeSensitive = auditOptions.Any(option => option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase));
        var overwrite = auditOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = auditOptions.Where(option => !option.StartsWith("--baseline=", StringComparison.OrdinalIgnoreCase) &&
            !option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown recovery-audit option: {unknown[0]}");
        var progress = new Progress<LegacyDatabaseAuditProgress>(value =>
            Console.Error.WriteLine(value.Table is null ? value.Stage : $"{value.Stage}\t{value.CompletedTables:N0}/{value.TotalTables:N0}\t{value.Table}\t{value.Rows:N0} change record(s)"));
        var result = await new LegacyDatabaseAuditService().AuditAsync(args[1], args[2], baseline,
            new(includes, excludes, includeSensitive, overwrite), progress, cancellationToken);
        foreach (var table in result.Manifest.Tables.Where(table => table.Status != LegacyDatabaseTableAuditStatus.Unchanged))
        {
            Console.WriteLine($"TABLE\t{table.Domain}\t{table.Status}\t{table.Name}\t+{table.AddedRows}\t~{table.ModifiedRows}\t-{table.RemovedRows}\t?{table.UnattributedRows}\t{table.ChangedFields} fields");
            foreach (var finding in table.Findings) Console.Error.WriteLine($"FINDING\t{table.Name}\t{finding}");
        }
        foreach (var warning in result.Manifest.Warnings) Console.Error.WriteLine($"WARNING: {warning}");
        Console.Error.WriteLine($"Legacy SQL recovery audit complete: {result.Manifest.TotalChangeRecords:N0} row record(s), {result.Manifest.TotalChangedFields:N0} field value(s), {result.Manifest.Tables.Count:N0} table(s).\nMode: {result.Manifest.Mode}; baseline identity: {result.Manifest.BaselineIdentity}.\nArtifact: {result.Path}\nThis is read-only evidence, not executable SQL.");
        return RecoveryAuditNeedsReview(result.Manifest) ? 3 : 0;
    }
    if (args[0].Equals("recovery-inspect", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2) return DatabaseHelp(2);
        var inspectOptions = args[2..];
        var quick = inspectOptions.Any(option => option.Equals("--quick", StringComparison.OrdinalIgnoreCase));
        var unknown = inspectOptions.Where(option => !option.Equals("--quick", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown recovery-inspect option: {unknown[0]}");
        var inspection = await new LegacyDatabaseAuditService().InspectAsync(args[1], verifyChanges: !quick, cancellationToken);
        if (inspection.Manifest is { } manifest)
        {
            var tables = manifest.Tables ?? [];
            Console.WriteLine($"Format\t{manifest.Format}\t{manifest.FormatVersion}\nCreatedUtc\t{manifest.CreatedUtc:O}\nMode\t{manifest.Mode}\nBaselineIdentity\t{manifest.BaselineIdentity}\nTables\t{tables.Count}\nChangeRecords\t{manifest.TotalChangeRecords}\nChangedFields\t{manifest.TotalChangedFields}\nChangesSha256\t{manifest.ChangesSha256}\nPromotionReady\t{manifest.PromotionReady}");
            foreach (var table in tables)
            {
                Console.WriteLine($"TABLE\t{table.Domain}\t{table.Status}\t{table.Name}\t{table.ChangeRecords}\t{table.ChangedFields}\t{string.Join(',', table.PrimaryKey ?? [])}");
                foreach (var finding in table.Findings ?? []) Console.Error.WriteLine($"FINDING\t{table.Name}\t{finding}");
            }
            foreach (var warning in manifest.Warnings ?? []) Console.Error.WriteLine($"WARNING: {warning}");
        }
        foreach (var finding in inspection.Findings) Console.Error.WriteLine($"INVALID\t{finding}");
        Console.Error.WriteLine(inspection.Valid ? $"Recovery audit is valid ({(quick ? "hash-only" : "full change-record verification")})." : "Recovery audit validation failed.");
        return !inspection.Valid ? 3 : inspection.Manifest is { } validManifest && RecoveryAuditNeedsReview(validManifest) ? 3 : 0;
    }
    if (args[0].Equals("snapshot-inspect", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2) return DatabaseHelp(2);
        var inspectOptions = args[2..];
        var quick = inspectOptions.Any(option => option.Equals("--quick", StringComparison.OrdinalIgnoreCase));
        var unknown = inspectOptions.Where(option => !option.Equals("--quick", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown snapshot-inspect option: {unknown[0]}");
        var inspection = await new LegacyDatabaseSnapshotService().InspectAsync(args[1], verifyRows: !quick, cancellationToken);
        if (inspection.Manifest is { } manifest)
        {
            Console.WriteLine($"Format\t{manifest.Format}\t{manifest.FormatVersion}\nCapturedUtc\t{manifest.CapturedUtc:O}\nDatabase\t{manifest.Source?.Database ?? "<missing>"}\nServer\t{manifest.Source?.ServerVersion ?? "<missing>"}\nTables\t{manifest.Tables?.Count ?? 0}\nRows\t{manifest.TotalRows}\nSchemaSha256\t{manifest.SchemaSha256}\nContentSha256\t{manifest.ContentSha256}\nConsistentSnapshot\t{manifest.ConsistentSnapshotStarted}\nReadOnlyTransaction\t{manifest.ReadOnlyTransactionEnforced}");
            if (manifest.Source?.CoreIdentity is not null) foreach (var identity in manifest.Source.CoreIdentity) Console.WriteLine($"CORE\t{identity.Key}\t{identity.Value}");
            if (manifest.Tables is not null) foreach (var table in manifest.Tables) Console.WriteLine($"TABLE\t{table.Name}\t{table.Rows}\t{table.Columns?.Count ?? 0}\t{string.Join(',', table.PrimaryKey ?? [])}\t{table.RowsSha256}");
        }
        foreach (var finding in inspection.Findings) Console.Error.WriteLine($"INVALID\t{finding}");
        Console.Error.WriteLine(inspection.Valid ? $"Snapshot is valid ({(quick ? "hash-only" : "full row-structure verification")})." : "Snapshot validation failed.");
        return inspection.Valid ? 0 : 3;
    }
    if (args.Length < 5) return DatabaseHelp(2);
    var operation = args[0]; var host = args[1]; var portText = args[2]; var user = args[3]; var database = args[4];
    if (operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase) && (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)))
        return Fail("db snapshot requires an output artifact path after the database name.");
    if (!uint.TryParse(portText, out var port) || port is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
    var rawOptions = operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase) && args.Length >= 6 ? args[6..] : args[5..];
    var passwordEnvironment = rawOptions.FirstOrDefault(option => option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase))?[15..] ?? "WOW_CRUCIBLE_DB_PASSWORD";
    var sslText = rawOptions.FirstOrDefault(option => option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase))?[6..] ?? "Preferred";
    if (string.IsNullOrWhiteSpace(passwordEnvironment)) return Fail("--password-env requires a non-empty environment-variable name.");
    var password = Environment.GetEnvironmentVariable(passwordEnvironment);
    if (password is null) return Fail($"Set the {passwordEnvironment} environment variable for this process. Passwords are not accepted on the command line.");
    if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(sslText, true, out var ssl)) return Fail($"Unknown SSL mode: {sslText}");
    var profile = new DatabaseConnectionProfile(host, port, user, password, database, ssl);
    if (operation.Equals("export", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db export requires a table name and output path.");
        var exportOptions = args[7..]; var formatText = Option(exportOptions, "--format=") ?? "csv"; var overwrite = exportOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = exportOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown export option: {unknown[0]}");
        var format = formatText.ToLowerInvariant() switch { "csv" => SqlExportFormat.Csv, "jsonl" or "json-lines" => SqlExportFormat.JsonLines, _ => throw new ArgumentException($"Unknown export format: {formatText}") };
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var progress = await new SqlTransferService().ExportTableAsync(profile, table, args[6], format, overwrite, cancellationToken); Console.Error.WriteLine($"Exported {progress.Rows:N0} row(s) from {progress.Table} to {progress.Path} ({progress.Format})."); return 0;
    }
    if (operation.Equals("import", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db import requires a table name and CSV path.");
        var importOptions = args[7..]; var apply = importOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = importOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown import option: {unknown[0]}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var transfer = new SqlTransferService(); var plan = transfer.AnalyzeCsv(args[6], table); Console.WriteLine($"Table\t{plan.Table}\nRows\t{plan.Rows}\nColumns\t{string.Join(',', plan.Columns)}\nCanApply\t{plan.CanApply}"); foreach (var finding in plan.Findings) Console.Error.WriteLine($"BLOCKED\t{finding}");
        if (!plan.CanApply) return 3; if (!apply) { Console.Error.WriteLine("Dry-run only. Re-run with --apply to insert all rows in one transaction; existing keys are never replaced."); return 0; }
        var inserted = await transfer.ImportCsvAsync(profile, table, plan.Path, cancellationToken); Console.Error.WriteLine($"Inserted {inserted:N0} row(s) transactionally into {table.Name}."); return 0;
    }
    if (operation.Equals("query", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db query requires a UTF-8 .sql file after the database name.");
        var queryOptions = args[6..]; var write = queryOptions.Any(option => option.Equals("--write", StringComparison.OrdinalIgnoreCase));
        var unknown = queryOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--write", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown query option: {unknown[0]}");
        var sql = await File.ReadAllTextAsync(args[5], cancellationToken);
        if (write)
        {
            var result = await new SqlWorkspaceService().ExecuteAsync(profile, sql, cancellationToken); Console.WriteLine($"AffectedRows\t{result.AffectedRows}\nDurationMs\t{result.Duration.TotalMilliseconds:0}"); return 0;
        }
        var query = await new SqlWorkspaceService().QueryAsync(profile, sql, 10000, cancellationToken); Console.WriteLine(string.Join('\t', query.Columns)); foreach (var row in query.Rows) Console.WriteLine(string.Join('\t', row.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))));
        Console.Error.WriteLine($"Returned {query.Rows.Count:N0} row(s) in {query.Duration.TotalMilliseconds:N0} ms. Use --write only for an intentionally reviewed non-query statement."); return 0;
    }
    if (operation.Equals("content-plan", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db content-plan requires a supported authoring domain and UTF-8 draft JSON path.");
        var planOptions = args[7..]; var apply = planOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var update = planOptions.Any(option => option.Equals("--update", StringComparison.OrdinalIgnoreCase)); var output = Option(planOptions, "--output="); var overwrite = planOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = planOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--update", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown content-plan option: {unknown[0]}"); if (update && !apply) return Fail("--update changes live data and therefore also requires --apply.");
        var json = await File.ReadAllTextAsync(args[6], cancellationToken); var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        WorldContentWritePlan plan = args[5].ToLowerInvariant() switch
        {
            "creature" => CreatureTemplateAdapter.CreatePlan(System.Text.Json.JsonSerializer.Deserialize<CreatureTemplateDraft>(json, jsonOptions) ?? throw new InvalidDataException("Creature draft JSON decoded to null."), capabilities),
            "gameobject" or "go" => GameObjectTemplateAdapter.CreatePlan(System.Text.Json.JsonSerializer.Deserialize<GameObjectTemplateDraft>(json, jsonOptions) ?? throw new InvalidDataException("Gameobject draft JSON decoded to null."), capabilities),
            "quest" => CreateQuestPlan(json, jsonOptions, capabilities),
            _ => CreateBehaviorPlan(args[5], json, jsonOptions, capabilities)
        };
        var sql = plan.PreviewSql() + Environment.NewLine; Console.Write(sql); foreach (var omitted in plan.OmittedFields) Console.Error.WriteLine($"OMITTED\t{omitted}");
        if (output is not null) { var fullPath = Path.GetFullPath(output); if (File.Exists(fullPath) && !overwrite) return Fail($"Output already exists: {fullPath}. Use --overwrite after reviewing it."); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!); await File.WriteAllTextAsync(fullPath, sql, cancellationToken); Console.Error.WriteLine($"SQL plan: {fullPath}"); }
        if (!apply) { Console.Error.WriteLine($"Dry-run only: {plan.Rows.Count:N0} row(s). Re-run with --apply to insert, or --apply --update to update the primary row and insert only new children."); return 0; }
        var content = new WorldContentTemplateService(); if (update) await content.UpdateFirstAndInsertChildrenAsync(profile, plan, cancellationToken); else await content.InsertAsync(profile, plan, cancellationToken); Console.Error.WriteLine($"Committed {plan.Domain}: primary {(update ? "updated" : "inserted")}, {plan.Rows.Count - 1:N0} child row(s) inserted transactionally."); return 0;
    }
    if (operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
    {
        var includes = rawOptions.Where(option => option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var excludes = rawOptions.Where(option => option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var includeSensitive = rawOptions.Any(option => option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase));
        var overwrite = rawOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = rawOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) &&
            !option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown snapshot option: {unknown[0]}");
        var progress = new Progress<LegacyDatabaseSnapshotProgress>(value =>
            Console.Error.WriteLine(value.Table is null ? $"{value.Stage}" : $"{value.Stage}\t{value.CompletedTables:N0}/{value.TotalTables:N0}\t{value.Table}\t{value.Rows:N0} rows"));
        var result = await new LegacyDatabaseSnapshotService().CaptureAsync(profile, args[5], new(includes, excludes, includeSensitive, overwrite), progress, cancellationToken);
        Console.Error.WriteLine($"Read-only legacy world snapshot complete: {result.Manifest.Tables.Count:N0} table(s), {result.Manifest.TotalRows:N0} row(s), {result.ArtifactBytes / (1024d * 1024):0.##} MiB.\nArtifact: {result.Path}\nSchema: {result.Manifest.SchemaSha256}\nContent: {result.Manifest.ContentSha256}\nConsistent snapshot: {result.Manifest.ConsistentSnapshotStarted}; database-enforced read-only: {result.Manifest.ReadOnlyTransactionEnforced}.\nExcluded by safety/filters: {result.Manifest.Policy.ExcludedTables.Count:N0} table(s).");
        return 0;
    }
    if (operation.Equals("inspect", StringComparison.OrdinalIgnoreCase))
    {
        var unknown = rawOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown database option: {unknown[0]}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile);
        Console.WriteLine($"Server\t{capabilities.ServerVersion}"); Console.WriteLine($"Database\t{capabilities.Database}");
        foreach (var table in capabilities.Tables.Values.OrderBy(table => table.Name)) Console.WriteLine($"TABLE\t{table.Name}\t{table.Columns.Count} columns");
        foreach (var relation in capabilities.Relationships.OrderBy(value => value.FromTable).ThenBy(value => value.Name)) Console.WriteLine($"RELATION\t{(relation.Declared ? "declared" : "inferred")}\t{relation.FromTable}.{relation.FromColumn}\t{relation.ToTable}.{relation.ToColumn}\t{relation.Description}");
        return capabilities.Tables.Count > 0 ? 0 : 1;
    }
    if (operation.Equals("item-audit", StringComparison.OrdinalIgnoreCase))
    {
        var output = Option(rawOptions, "--output="); var dbc = Option(rawOptions, "--dbc="); var unknown = rawOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-audit option: {unknown[0]}");
        var audit = await new ItemCatalogService().AuditAsync(profile, dbc);
        foreach (var item in audit.NoKnownAcquisitionPath) Console.WriteLine($"{item.Entry}\t{item.Quality}\t{item.ItemLevel}\t{item.ItemSetId}\t{item.Name}");
        if (output is not null) File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(audit, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"Item acquisition audit: {audit.NoKnownAcquisitionPath.Count:N0} of {audit.TotalItems:N0} item(s) have no known path across {audit.CheckedSources.Count:N0} available source table(s). Missing source families: {string.Join(", ", audit.MissingSources)}{(output is null ? string.Empty : $". Report: {Path.GetFullPath(output)}")}");
        return 0;
    }
    if (operation.Equals("item-inspect", StringComparison.OrdinalIgnoreCase) && args.Length >= 6 && uint.TryParse(args[5], out var inspectedEntry))
    {
        var inspectOptions = args[6..]; var dbc = Option(inspectOptions, "--dbc="); var unknown = inspectOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-inspect option: {unknown[0]}");
        var inspection = await new ItemCatalogService().InspectAsync(profile, inspectedEntry, dbc);
        if (inspection.Item is null) return Fail($"Item {inspectedEntry} does not exist in item_template.");
        Console.WriteLine($"ITEM\t{inspection.Item.Entry}\t{inspection.Item.Name}");
        Console.WriteLine($"CLASSIFICATION\t{(inspection.HasKnownAcquisitionPath ? "KNOWN ACQUISITION PATH" : "NO KNOWN ACQUISITION PATH")}");
        foreach (var evidence in inspection.AcceptedEvidence) Console.WriteLine($"ACCEPTED\t{evidence}");
        foreach (var evidence in inspection.RejectedEvidence) Console.WriteLine($"REJECTED\t{evidence}");
        Console.WriteLine($"COVERAGE\t{inspection.CheckedSources.Count} checked\t{inspection.MissingSources.Count} missing");
        foreach (var missing in inspection.MissingSources) Console.WriteLine($"MISSING\t{missing}");
        return 0;
    }
    if (operation.Equals("item-clone", StringComparison.OrdinalIgnoreCase) && args.Length >= 7 && uint.TryParse(args[5], out var sourceEntry) && uint.TryParse(args[6], out var newEntry))
    {
        var cloneOptions = args[7..]; var suffix = Option(cloneOptions, "--suffix=") ?? " Variant"; var setText = Option(cloneOptions, "--itemset=");
        var unknown = cloneOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--suffix=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--itemset=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-clone option: {unknown[0]}");
        uint? itemSet = setText is null ? null : uint.Parse(setText, System.Globalization.CultureInfo.InvariantCulture);
        var result = await new ItemCatalogService().CloneAsync(profile, sourceEntry, newEntry, suffix, itemSet);
        Console.WriteLine($"Source\t{result.SourceEntry}\t{result.SourceName}\nClone\t{result.NewEntry}\t{result.NewName}\nItemSet\t{result.ItemSetId}\nColumns\t{result.CopiedColumns}\nLocaleRows\t{result.CopiedLocaleRows}"); return 0;
    }
    return DatabaseHelp(2);
}

static WorldContentWritePlan CreateQuestPlan(string json, System.Text.Json.JsonSerializerOptions options, DatabaseCapabilities capabilities)
{
    var draft = System.Text.Json.JsonSerializer.Deserialize<QuestPortableDraft>(json, options) ?? throw new InvalidDataException("Quest draft JSON decoded to null."); var table = capabilities.FindTable("quest_template") ?? throw new NotSupportedException("The connected schema has no quest_template table."); var values = draft.Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase); return QuestTemplateAdapter.CreatePlan(table, values, capabilities, draft.Links);
}

static BehaviorPortableDraft CreateBehaviorDraft(string id)
{
    var domain = BehaviorDomainCatalog.Find(id); var table = BehaviorAuthoringAdapter.PortableTable(domain.TableName); var values = BehaviorAuthoringAdapter.Defaults(table).ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase); return new(domain.Id, values);
}

static WorldContentWritePlan CreateBehaviorPlan(string id, string json, System.Text.Json.JsonSerializerOptions options, DatabaseCapabilities capabilities)
{
    var draft = System.Text.Json.JsonSerializer.Deserialize<BehaviorPortableDraft>(json, options) ?? throw new InvalidDataException("Behavior draft JSON decoded to null."); var requested = BehaviorDomainCatalog.Find(id); var embedded = BehaviorDomainCatalog.Find(draft.Domain); if (!requested.Id.Equals(embedded.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Draft domain '{embedded.Id}' does not match requested domain '{requested.Id}'."); var table = capabilities.FindTable(requested.TableName) ?? throw new NotSupportedException($"The connected schema has no {requested.TableName} table."); var values = draft.Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase); return BehaviorAuthoringAdapter.CreatePlan(requested, table, values);
}

static int DatabaseHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible db draft-template <domain> <output.json> [--overwrite]\n  wowcrucible db inspect <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db query <host> <port> <user> <database> <statement.sql> [--write] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db export <host> <port> <user> <database> <table> <output> [--format=csv|jsonl] [--overwrite] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db import <host> <port> <user> <database> <table> <input.csv> [--apply] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db content-plan <host> <port> <user> <database> <domain> <draft.json> [--output=plan.sql] [--overwrite] [--apply] [--update] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db snapshot <host> <port> <user> <database> <output.crucible-db-snapshot> [--password-env=NAME] [--ssl=Preferred] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]\n  wowcrucible db snapshot-inspect <snapshot-file> [--quick]\n  wowcrucible db recovery-audit <legacy-snapshot> <output.crucible-db-audit> [--baseline=stock-snapshot] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]\n  wowcrucible db recovery-inspect <audit-file> [--quick]\n  wowcrucible db item-audit <host> <port> <user> <database> [--password-env=NAME] [--dbc=folder] [--output=report.json]\n  wowcrucible db item-inspect <host> <port> <user> <database> <item-id> [--password-env=NAME] [--dbc=folder]\n  wowcrucible db item-clone <host> <port> <user> <database> <source-id> <new-id> [--suffix=\" Variant\"] [--itemset=ID]\n\nDomains: creature, gameobject, quest, gossip-menu, gossip-option, npc-text, trainer, trainer-spell, trainer-creature, legacy-trainer-spell, condition, smartai.\n\nDraft templates and content-plan provide portable complete-field authoring automation. content-plan is dry-run by default; --apply inserts, while --apply --update updates exactly one primary row and inserts collision-free children in one transaction. Snapshot capture is SELECT-only and excludes known auth/character runtime state by default. query reads SQL from a file so statements and secrets do not need to enter shell history; --write is explicit. import is a dry-run unless --apply is present, is INSERT-only, and rolls back the complete CSV on any duplicate/error. export streams the complete table. item-inspect explains accepted and rejected SQL/DBC acquisition evidence for one exact item ID. recovery-audit is completely offline: with a baseline it records baseline-to-legacy deltas; without one it labels rows unattributed candidates. No recovery audit is executable SQL, no-PK tables are blocked from row inference, and removals are never implicitly approved. --include-sensitive is an explicit override. Passwords are read from WOW_CRUCIBLE_DB_PASSWORD by default and are never accepted as command arguments.", code);

static bool RecoveryAuditNeedsReview(LegacyDatabaseAuditManifest manifest) =>
    manifest.Mode == LegacyDatabaseAuditMode.Unattributed ||
    manifest.BaselineIdentity != LegacyDatabaseBaselineIdentity.MatchingCoreIdentity ||
    (manifest.Warnings?.Count ?? 0) > 1 ||
    (manifest.Tables ?? []).Any(table => table.Status is LegacyDatabaseTableAuditStatus.BlockedNoPrimaryKey or
        LegacyDatabaseTableAuditStatus.BlockedIncompatibleSchema or LegacyDatabaseTableAuditStatus.NotCaptured or
        LegacyDatabaseTableAuditStatus.SchemaChanged or LegacyDatabaseTableAuditStatus.BaselineTableOnly ||
        table.RemovedRows > 0 || (table.Findings?.Count ?? 0) > 0);

static int Manifest(string[] args)
{
    if (args.Length > 0 && args[0] is "help" or "--help" or "-h") return ManifestHelp();
    if (args is ["create", var manifestPath, var outputFile, .. var rawInputs] && rawInputs.Length > 0)
    {
        var executableOption = rawInputs.FirstOrDefault(value => value.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase));
        var allowed = rawInputs.Where(value => value.StartsWith("--allow=", StringComparison.OrdinalIgnoreCase)).Select(value => value[8..]).ToArray();
        var forbidden = rawInputs.Where(value => value.StartsWith("--deny=", StringComparison.OrdinalIgnoreCase)).Select(value => value[7..]).ToArray();
        var required = rawInputs.Where(value => value.StartsWith("--require=", StringComparison.OrdinalIgnoreCase)).Select(value => value[10..]).ToArray();
        var countOption = rawInputs.FirstOrDefault(value => value.StartsWith("--count=", StringComparison.OrdinalIgnoreCase));
        var unknown = rawInputs.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--allow=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--deny=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--require=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--count=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown manifest option: {unknown[0]}");
        var inputs = rawInputs.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (inputs.Length == 0) return Fail("Add at least one file or folder to the manifest.");
        var entries = PatchInputMapper.Map(inputs);
        var hash = executableOption is null ? null : PatchManifestService.ComputeExecutableSha256(executableOption[13..]);
        var policy = allowed.Length == 0 && forbidden.Length == 0 && required.Length == 0 && countOption is null ? null : new PatchManifestPolicy(allowed, forbidden, countOption is null ? null : int.Parse(countOption[8..]), required);
        PatchManifestService.Save(manifestPath, Path.GetFileNameWithoutExtension(manifestPath), outputFile, entries, hash, policy);
        PrintCompatibility(entries, hash);
        return 0;
    }
    switch (args)
    {
        case ["build", var buildManifestPath, var outputDirectory]:
            var manifest = PatchManifestService.Load(buildManifestPath);
            PrintCompatibility(manifest.Entries, manifest.RequiredClientExecutableSha256);
            PatchManifestService.Build(buildManifestPath, outputDirectory); return 0;
        case ["list", var listManifestPath]:
            var listManifest = PatchManifestService.Load(listManifestPath);
            foreach (var entry in listManifest.Entries.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase)) Console.WriteLine($"{entry.SourcePath}\t{entry.ArchivePath}");
            Console.Error.WriteLine($"Dry run: {listManifest.Entries.Count:N0} source-to-archive mapping(s), output {listManifest.OutputFileName}.");
            return 0;
        case ["validate", var validateManifestPath]:
            return PrintManifestValidation(PatchManifestService.Validate(PatchManifestService.Load(validateManifestPath)));
        case ["validate", var validateArchiveManifestPath, var archivePath]:
            return PrintManifestValidation(PatchManifestService.Validate(PatchManifestService.Load(validateArchiveManifestPath), archivePath));
        default:
            return ManifestHelp(2);
    }
}

static int Dbc(string[] args)
{
    if (args.Length > 0 && args[0] is "help" or "--help" or "-h") return DbcHelp();
    if (args is ["itemset", "inspect", var itemSetPath, var itemSetSchema, var itemSetIdText, .. var itemSetOptions] && uint.TryParse(itemSetIdText, out var itemSetId))
    {
        var spellPath = Option(itemSetOptions, "--spell="); var unknown = itemSetOptions.Where(option => !option.StartsWith("--spell=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown itemset inspect option: {unknown[0]}");
        var set = ItemSetDbcService.Inspect(itemSetPath, itemSetSchema, itemSetId, spellPath);
        Console.WriteLine($"ID\t{set.Id}\nName\t{set.Name}\nRequiredSkill\t{set.RequiredSkill}\nRequiredSkillRank\t{set.RequiredSkillRank}\nItems\t{string.Join(",", set.ItemIds)}");
        foreach (var effect in set.Effects) Console.WriteLine($"EFFECT\t{effect.Slot}\t{effect.RequiredItems}\t{effect.SpellId}\t{effect.SpellName ?? "unknown spell"}"); return 0;
    }
    if (args is ["itemset", "clone", var cloneItemSetPath, var cloneItemSetSchema, var cloneItemSetOutput, var sourceSetText, var newSetText, .. var itemSetCloneOptions] && uint.TryParse(sourceSetText, out var sourceSet) && uint.TryParse(newSetText, out var newSet))
    {
        var mapText = Option(itemSetCloneOptions, "--map=") ?? throw new ArgumentException("Item-set cloning requires --map=old:new,old:new for every member."); var suffix = Option(itemSetCloneOptions, "--suffix=") ?? " Variant";
        var unknown = itemSetCloneOptions.Where(option => !option.StartsWith("--map=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--suffix=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown itemset clone option: {unknown[0]}");
        var map = mapText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(pair => pair.Split(':')).ToDictionary(pair => uint.Parse(pair[0]), pair => uint.Parse(pair[1]));
        var result = ItemSetDbcService.Clone(cloneItemSetPath, cloneItemSetSchema, cloneItemSetOutput, sourceSet, newSet, map, suffix);
        Console.Error.WriteLine($"Cloned item set {result.SourceSetId} to {result.NewSetId} '{result.Name}' with {result.ItemIdMap.Count:N0} remapped member(s): {result.OutputPath}"); return 0;
    }
    if (args is ["itemset", "effects", var effectItemSetPath, var effectItemSetSchema, var effectItemSetOutput, var effectSetText, .. var effectOptions] && uint.TryParse(effectSetText, out var effectSet))
    {
        var unknown = effectOptions.Where(option => !option.StartsWith("--effect=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown itemset effects option: {unknown[0]}");
        var effects = effectOptions.Where(option => option.StartsWith("--effect=", StringComparison.OrdinalIgnoreCase)).Select((option, index) =>
        {
            var pair = option[9..].Split(':'); if (pair.Length != 2) throw new ArgumentException("Each --effect must be required-items:spell-id."); return new ItemSetEffect(index + 1, uint.Parse(pair[0]), uint.Parse(pair[1]), null);
        }).ToArray();
        ItemSetDbcService.SetEffects(effectItemSetPath, effectItemSetSchema, effectItemSetOutput, effectSet, effects); Console.Error.WriteLine($"Wrote {effects.Length:N0} item-set effect slot(s) for set {effectSet}: {Path.GetFullPath(effectItemSetOutput)}"); return 0;
    }
    if (args is ["rows", var rowsPath, var rowsSchemaPath, .. var rawIds] && rawIds.Length > 0)
    {
        var rowsFile = WdbcFile.Load(rowsPath); var tableName = Path.GetFileNameWithoutExtension(rowsPath);
        var resolution = DbcSchemaCatalog.Load(rowsSchemaPath).ResolveColumns(tableName, rowsFile.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, rowsFile.FieldCount, resolution));
        var indexed = DbcRecordIdentity.IndexRows(rowsFile, resolution.Columns, resolution.KeyStrategy); var rows = new List<object>();
        foreach (var rawId in rawIds)
        {
            if (!uint.TryParse(rawId, out var id)) return Fail($"Invalid row ID: {rawId}");
            if (!indexed.TryGetValue(id, out var row)) { rows.Add(new { Id = id, Missing = true, Values = new Dictionary<string, object?>() }); continue; }
            rows.Add(new { Id = id, Missing = false, Values = resolution.Columns.ToDictionary(column => column.Name, column => rowsFile.GetDisplayValue(row, column)) });
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return rows.Any() ? 0 : 3;
    }
    if (args is ["find", var findPath, var findSchemaPath, var findColumnName, .. var findValues] && findValues.Length > 0)
    {
        var countOnly = findValues.Contains("--count", StringComparer.OrdinalIgnoreCase);
        var limitOption = findValues.FirstOrDefault(value => value.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase));
        var unknown = findValues.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.Equals("--count", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown DBC find option: {unknown[0]}");
        var requestedValues = findValues.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (requestedValues.Length == 0) return Fail("DBC find requires at least one value.");
        var limit = limitOption is null ? int.MaxValue : int.Parse(limitOption[8..]);
        if (limit < 1) return Fail("DBC find limit must be positive.");
        var findFile = WdbcFile.Load(findPath); var tableName = Path.GetFileNameWithoutExtension(findPath);
        var resolution = DbcSchemaCatalog.Load(findSchemaPath).ResolveColumns(tableName, findFile.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, findFile.FieldCount, resolution));
        var column = resolution.Columns.FirstOrDefault(value => value.Name.Equals(findColumnName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{tableName} has no named column '{findColumnName}'.");
        var wanted = requestedValues.ToHashSet(StringComparer.OrdinalIgnoreCase); var matches = new List<uint>();
        foreach (var (id, row) in DbcRecordIdentity.IndexRows(findFile, resolution.Columns, resolution.KeyStrategy).OrderBy(pair => pair.Key))
        {
            var value = Convert.ToString(findFile.GetDisplayValue(row, column), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (wanted.Contains(value)) matches.Add(id);
        }
        if (countOnly) Console.WriteLine(matches.Count);
        else Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(matches.Take(limit), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"Found {matches.Count:N0} {tableName} record(s) whose {column.Name} matches {wanted.Count:N0} requested value(s).");
        return matches.Count > 0 ? 0 : 3;
    }
    if (args.Length is 4 or 5 && args[0] == "compare" && (args.Length == 4 || args[4] == "--summary"))
    {
        var basePath = args[1]; var overridePath = args[2]; var schemaPath = args[3];
        var tableName = Path.GetFileNameWithoutExtension(basePath);
        var sample = WdbcFile.Load(basePath);
        var resolution = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(tableName, sample.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        var differences = DbcPromotionService.GetDifferences(basePath, overridePath, resolution.Columns, resolution.KeyStrategy);
        if (args.Length == 5)
        {
            var overrideFile = WdbcFile.Load(overridePath);
            var baseIds = DbcRecordIdentity.IndexRows(sample, resolution.Columns, resolution.KeyStrategy).Keys;
            var overrideIds = DbcRecordIdentity.IndexRows(overrideFile, resolution.Columns, resolution.KeyStrategy).Keys.ToHashSet();
            var removedRows = baseIds.Count(id => !overrideIds.Contains(id));
            Console.WriteLine($"{tableName}\t{sample.RowCount}\t{overrideFile.RowCount}\t{differences.Select(difference => difference.Id).Distinct().Count()}\t{differences.Count}\t{differences.Count(difference => difference.ColumnIndex < 0)}\t{removedRows}");
        }
        else foreach (var difference in differences) Console.WriteLine($"{difference.Id}\t{difference.ColumnName}\t{difference.BaseValue}\t{difference.OverrideValue}");
        Console.Error.WriteLine($"Found {differences.Count:N0} semantic field difference(s)/new row marker(s).");
        return 0;
    }
    if (args is ["promote", "apply", var promotionBasePath, var promotionOverridePath, var promotionSchemaPath, var manifestPath, var outputPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(promotionBasePath);
        var sample = WdbcFile.Load(promotionBasePath);
        var resolution = DbcSchemaCatalog.Load(promotionSchemaPath).ResolveColumns(tableName, sample.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        DbcPromotionService.Apply(promotionBasePath, promotionOverridePath, outputPath, resolution.Columns, resolution.KeyStrategy, DbcPromotionService.LoadManifest(manifestPath));
        Console.Error.WriteLine($"Created promoted DBC: {Path.GetFullPath(outputPath)}");
        return 0;
    }
    if (args is ["promote", "additions", var additionsBasePath, var additionsOverridePath, var additionsSchemaPath, var additionsManifestPath, var additionsOutputPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(additionsBasePath); var sample = WdbcFile.Load(additionsBasePath);
        var resolution = DbcSchemaCatalog.Load(additionsSchemaPath).ResolveColumns(tableName, sample.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        var manifest = DbcPromotionService.CreateAdditionsManifest(additionsBasePath, additionsOverridePath, resolution.Columns, resolution.KeyStrategy);
        if (manifest.Operations.Count == 0) { Console.Error.WriteLine($"{tableName} contains no IDs absent from the base; no output was written."); return 3; }
        DbcPromotionService.SaveManifest(additionsManifestPath, manifest);
        DbcPromotionService.Apply(additionsBasePath, additionsOverridePath, additionsOutputPath, resolution.Columns, resolution.KeyStrategy, manifest);
        Console.Error.WriteLine($"Added {manifest.Operations.Count:N0} previously absent {tableName} record(s) without modifying existing IDs. Manifest: {Path.GetFullPath(additionsManifestPath)}. Output: {Path.GetFullPath(additionsOutputPath)}");
        return 0;
    }
    if (args is ["clone-remap", "where", var cloneBasePath, var cloneSourcePath, var cloneSchemaPath, var cloneColumnName, .. var cloneArguments])
    {
        var cloneManifestPath = Option(cloneArguments, "--manifest="); var cloneOutputPath = Option(cloneArguments, "--output="); var startText = Option(cloneArguments, "--start-id=");
        if (cloneManifestPath is null || cloneOutputPath is null) return Fail("Clone/remap requires --manifest=plan.json and --output=merged.dbc.");
        uint? startId = startText is null ? null : uint.TryParse(startText, out var parsedStart) ? parsedStart : throw new ArgumentException("Clone/remap start ID must be an unsigned integer.");
        var values = cloneArguments.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = cloneArguments.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.StartsWith("--manifest=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--start-id=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown clone/remap option: {unknown[0]}");
        if (values.Count == 0) return Fail("Clone/remap where requires at least one field value.");
        var sourceFile = WdbcFile.Load(cloneSourcePath); var tableName = Path.GetFileNameWithoutExtension(cloneBasePath);
        if (!tableName.Equals(Path.GetFileNameWithoutExtension(cloneSourcePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Base and source DBC table names differ.");
        var resolution = DbcSchemaCatalog.Load(cloneSchemaPath).ResolveColumns(tableName, sourceFile.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sourceFile.FieldCount, resolution));
        var column = resolution.Columns.FirstOrDefault(value => value.Name.Equals(cloneColumnName, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{tableName} has no named column '{cloneColumnName}'.");
        var sourceIds = DbcRecordIdentity.IndexRows(sourceFile, resolution.Columns, resolution.KeyStrategy).Where(pair => values.Contains(Convert.ToString(sourceFile.GetDisplayValue(pair.Value, column), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)).Select(pair => pair.Key).ToArray();
        var manifest = DbcCloneRemapService.CreateManifest(cloneBasePath, cloneSourcePath, resolution.Columns, resolution.KeyStrategy, sourceIds, startId);
        DbcCloneRemapService.Save(cloneManifestPath, manifest); DbcCloneRemapService.Apply(cloneBasePath, cloneSourcePath, cloneOutputPath, resolution.Columns, resolution.KeyStrategy, manifest);
        var cloned = manifest.Entries.Count(entry => !entry.ReusesExisting); var reused = manifest.Entries.Count - cloned; var identical = sourceIds.Distinct().Count() - manifest.Entries.Count;
        Console.Error.WriteLine($"{tableName}: added/cloned {cloned:N0}, reused {reused:N0} equivalent existing record(s), skipped {identical:N0} identical same-ID record(s). Existing records were not modified. Mapping: {Path.GetFullPath(cloneManifestPath)}");
        return 0;
    }
    if (args is ["clone-dependency", var parentSourcePath, var parentMergedPath, var parentSchemaPath, var parentMapPath, var foreignColumnName, var childBasePath, var childSourcePath, var childSchemaPath, .. var dependencyOptions])
    {
        var childMapPath = Option(dependencyOptions, "--child-map="); var childOutputPath = Option(dependencyOptions, "--child-output="); var parentOutputPath = Option(dependencyOptions, "--parent-output=");
        if (childMapPath is null || childOutputPath is null || parentOutputPath is null) return Fail("Clone dependency requires --child-map=, --child-output=, and --parent-output= paths.");
        var unknown = dependencyOptions.Where(value => !value.StartsWith("--child-map=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--child-output=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--parent-output=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown clone-dependency option: {unknown[0]}");
        var parentMap = DbcCloneRemapService.Load(parentMapPath); var parentSource = WdbcFile.Load(parentSourcePath); var parentTable = Path.GetFileNameWithoutExtension(parentSourcePath);
        var parentResolution = DbcSchemaCatalog.Load(parentSchemaPath).ResolveColumns(parentTable, parentSource.FieldCount);
        if (parentResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(parentTable, parentSource.FieldCount, parentResolution));
        var childBase = WdbcFile.Load(childBasePath); var childTable = Path.GetFileNameWithoutExtension(childBasePath);
        var childResolution = DbcSchemaCatalog.Load(childSchemaPath).ResolveColumns(childTable, childBase.FieldCount);
        if (childResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(childTable, childBase.FieldCount, childResolution));
        var referencedIds = DbcCloneRemapService.FindReferencedIds(parentSourcePath, parentResolution.Columns, parentResolution.KeyStrategy, parentMap.Entries.Select(entry => entry.SourceId), foreignColumnName);
        var childSource = WdbcFile.Load(childSourcePath); var childBaseRows = DbcRecordIdentity.IndexRows(childBase, childResolution.Columns, childResolution.KeyStrategy); var childSourceRows = DbcRecordIdentity.IndexRows(childSource, childResolution.Columns, childResolution.KeyStrategy);
        var changedReferencedIds = referencedIds.Where(id => childSourceRows.TryGetValue(id, out var sourceRow) && (!childBaseRows.TryGetValue(id, out var baseRow) || !DbcRowsEqual(childBase, baseRow, childSource, sourceRow, childResolution.Columns))).ToArray();
        if (changedReferencedIds.Length == 0) throw new InvalidOperationException($"No referenced {childTable} records differ from the baseline; the cloned parent can keep its existing references.");
        var childMap = DbcCloneRemapService.CreateManifest(childBasePath, childSourcePath, childResolution.Columns, childResolution.KeyStrategy, changedReferencedIds);
        DbcCloneRemapService.Save(childMapPath, childMap); DbcCloneRemapService.Apply(childBasePath, childSourcePath, childOutputPath, childResolution.Columns, childResolution.KeyStrategy, childMap);
        var changed = DbcCloneRemapService.ApplyReferenceMap(parentMergedPath, parentOutputPath, parentResolution.Columns, parentResolution.KeyStrategy, parentMap.Entries.Where(entry => !entry.ReusesExisting).Select(entry => entry.TargetId), foreignColumnName, childMap);
        Console.Error.WriteLine($"Added/cloned {childMap.Entries.Count(entry => !entry.ReusesExisting):N0} and reused {childMap.Entries.Count(entry => entry.ReusesExisting):N0} referenced {childTable} record(s); rewrote {changed:N0} newly added {parentTable}.{foreignColumnName} reference(s). Baseline records were not modified.");
        return 0;
    }
    if (args is ["copy-row", var copyBasePath, var copySourcePath, var copySchemaPath, var copySourceIdText, var copyTargetIdText, var copyOutputPath, .. var copyOptions])
    {
        if (!uint.TryParse(copySourceIdText, out var copySourceId) || !uint.TryParse(copyTargetIdText, out var copyTargetId)) return Fail("Source and target IDs must be unsigned integers.");
        var copyValues = ParseSetOptions(copyOptions); var copySample = WdbcFile.Load(copyBasePath); var copyTable = Path.GetFileNameWithoutExtension(copyBasePath);
        var copyResolution = DbcSchemaCatalog.Load(copySchemaPath).ResolveColumns(copyTable, copySample.FieldCount);
        if (copyResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(copyTable, copySample.FieldCount, copyResolution));
        DbcRowMutationService.CopyRow(copyBasePath, copySourcePath, copyOutputPath, copyResolution.Columns, copyResolution.KeyStrategy, copySourceId, copyTargetId, copyValues);
        Console.Error.WriteLine($"Copied {copyTable} ID {copySourceId} to additive ID {copyTargetId} with {copyValues.Count:N0} field override(s): {Path.GetFullPath(copyOutputPath)}");
        return 0;
    }
    if (args is ["set-row", var setInputPath, var setSchemaPath, var setIdText, var setOutputPath, .. var setOptions])
    {
        if (!uint.TryParse(setIdText, out var setId)) return Fail("Record ID must be an unsigned integer.");
        var setValues = ParseSetOptions(setOptions); var setSample = WdbcFile.Load(setInputPath); var setTable = Path.GetFileNameWithoutExtension(setInputPath);
        var setResolution = DbcSchemaCatalog.Load(setSchemaPath).ResolveColumns(setTable, setSample.FieldCount);
        if (setResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(setTable, setSample.FieldCount, setResolution));
        DbcRowMutationService.SetRow(setInputPath, setOutputPath, setResolution.Columns, setResolution.KeyStrategy, setId, setValues);
        Console.Error.WriteLine($"Updated {setTable} ID {setId} in an output copy with {setValues.Count:N0} field value(s): {Path.GetFullPath(setOutputPath)}");
        return 0;
    }
    if (args.Length >= 3 && args[0] == "validate")
    {
        var options = args[3..];
        var unknown = options.Where(option => !option.Equals("--strict", StringComparison.OrdinalIgnoreCase) && !option.Equals("--recursive", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown validate option: {unknown[0]}. Supported: --strict, --recursive");
        var strict = options.Any(option => option.Equals("--strict", StringComparison.OrdinalIgnoreCase));
        var recursive = options.Any(option => option.Equals("--recursive", StringComparison.OrdinalIgnoreCase));
        var results = DbcCorpusValidator.Validate(args[1], args[2], recursive: recursive);
        foreach (var result in results) Console.WriteLine($"{(result.Skipped ? "SKIP" : !result.Passed ? "FAIL" : result.Warning ? "WARN" : "PASS")}\t{result.Rows}\t{result.Fields}\t{result.Path}\t{result.Message}");
        Console.Error.WriteLine($"Validated {results.Count:N0} DBC paths: {results.Count(result => result.Passed && !result.Skipped && !result.Warning):N0} named-schema passes, {results.Count(result => result.Warning):N0} fallbacks, {results.Count(result => result.Skipped):N0} skipped, {results.Count(result => !result.Passed):N0} failed.");
        return results.All(result => result.Passed) && (!strict || results.All(result => !result.Warning)) ? 0 : 1;
    }
    if (args.Length != 2 || args[0] != "info") return DbcHelp(2);
    var file = WdbcFile.Load(args[1]);
    Console.WriteLine($"Path\t{Path.GetFullPath(args[1])}");
    Console.WriteLine($"Rows\t{file.RowCount}"); Console.WriteLine($"Fields\t{file.FieldCount}"); Console.WriteLine($"StringBytes\t{file.StringTableSize}");
    return 0;
}

static int Mpq(string[] args)
{
    if (args.Length == 0) return MpqHelp(2);
    if (args[0] is "help" or "--help" or "-h" || args.Length > 1 && args[1] is "--help" or "-h") return MpqHelp();
    var service = new PatchArchiveService();
    switch (args[0])
    {
        case "list" when args.Length >= 2:
            {
                var options = args[2..]; var query = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
                var contentOnly = options.Any(option => option.Equals("--content-only", StringComparison.OrdinalIgnoreCase));
                var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
                var listFile = Option(options, "--listfile=");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--content-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown list option: {unknown[0]}");
                var allFiles = service.ListFiles(args[1], "*", listFile);
                var files = allFiles.Where(file => (!contentOnly || !file.IsMetadata) && MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(files, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                else foreach (var file in files) Console.WriteLine($"{file.Size}\t{file.CompressedSize}\t{file.ArchivePath}");
                PrintAnonymousMpqWarning(allFiles, listFile);
                return 0;
            }
        case "extract" when args.Length >= 3:
            {
                var options = args[3..];
                var query = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
                var quiet = options.Any(option => option.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
                var listFile = Option(options, "--listfile=");
                var progressOption = options.FirstOrDefault(option => option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase));
                var progressStep = progressOption is null ? 5 : int.Parse(progressOption[11..]);
                if (progressStep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(progressStep), "Progress percentage must be from 1 to 100.");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown extract option: {unknown[0]}");
                var allFiles = service.ListFiles(args[1], "*", listFile);
                var files = allFiles.Where(file => MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
                PrintAnonymousMpqWarning(allFiles, listFile);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                service.Extract(args[1], args[2], files, quiet ? null : new ConsoleProgress(progressStep));
                Console.Error.WriteLine($"Extracted {files.Length:N0} file(s) to {Path.GetFullPath(args[2])} in {timer.Elapsed.TotalSeconds:0.##}s.");
                return 0;
            }
        case "merge" when args.Length >= 4:
            {
                var options = args[2..]; var inputArchives = options.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray();
                var listFile = Option(options, "--listfile="); var conflictText = Option(options, "--conflicts=") ?? "block";
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--conflicts=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown merge option: {unknown[0]}");
                var policy = conflictText.ToLowerInvariant() switch { "block" => MpqMergeConflictPolicy.BlockDifferentEntries, "earlier" => MpqMergeConflictPolicy.PreferEarlierArchive, "later" => MpqMergeConflictPolicy.PreferLaterArchive, _ => throw new ArgumentException("--conflicts must be block, earlier, or later.") };
                var result = new MpqMergeService().Merge(inputArchives, args[1], policy, listFile, new Progress<(int Done, int Total, string Path)>(value => Console.Error.WriteLine($"Merge\t{value.Done:N0}/{value.Total:N0}\t{value.Path}")));
                foreach (var conflict in result.Conflicts) Console.WriteLine($"CONFLICT\t{conflict.ArchivePath}\t{string.Join('|', conflict.Sources)}\t{string.Join('|', conflict.Sha256)}");
                if (result.OutputFiles == 0 && result.Conflicts.Count > 0) { Console.Error.WriteLine($"Merge blocked by {result.Conflicts.Count:N0} different-byte internal path conflict(s); source archives and output were not modified."); return 3; }
                Console.Error.WriteLine($"Merged {result.InputArchives:N0} source patches into {result.OutputPath}: {result.OutputFiles:N0} files, {result.ExactDuplicates:N0} exact duplicate(s), {result.Conflicts.Count:N0} explicitly resolved conflict(s) using {result.Policy}."); return 0;
            }
        case "create" when args.Length >= 3:
            var createEntries = PatchInputMapper.Map(args[2..]); PrintCompatibility(createEntries, null); service.Create(args[1], createEntries); return 0;
        case "update" when args.Length >= 3:
            var updateEntries = PatchInputMapper.Map(args[2..]); PrintCompatibility(updateEntries, null); service.Update(args[1], updateEntries); return 0;
        default:
            return MpqHelp(2);
    }
}

static int ManifestHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible manifest create <manifest.json> <output.mpq> <files/folders...> [--allow=glob] [--deny=glob] [--require=glob] [--count=N] [--client-exe=Wow.exe]\n  wowcrucible manifest list <manifest.json>\n  wowcrucible manifest validate <manifest.json> [archive.mpq]\n  wowcrucible manifest build <manifest.json> <output-folder>", code);
static int DbcHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible dbc info <file.dbc>\n  wowcrucible dbc rows <file.dbc> <schema.xml> <id>...\n  wowcrucible dbc find <file.dbc> <schema.xml> <column> <value>... [--count|--limit=N]\n  wowcrucible dbc validate <schema.xml> <dbc-folder> [--strict] [--recursive]\n  wowcrucible dbc compare <base.dbc> <override.dbc> <schema.xml> [--summary]\n  wowcrucible dbc promote apply <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>\n  wowcrucible dbc promote additions <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>\n  wowcrucible dbc clone-remap where <base.dbc> <source.dbc> <schema.xml> <column> <value>... --manifest=map.json --output=merged.dbc [--start-id=N]\n  wowcrucible dbc clone-dependency <parent-source.dbc> <parent-merged.dbc> <parent-schema.xml> <parent-map.json> <foreign-column> <child-base.dbc> <child-source.dbc> <child-schema.xml> --child-map=map.json --child-output=child.dbc --parent-output=parent.dbc\n  wowcrucible dbc copy-row <base.dbc> <source.dbc> <schema.xml> <source-id> <target-id> <output.dbc> [--set=Column=Value]...\n  wowcrucible dbc set-row <input.dbc> <schema.xml> <id> <output.dbc> --set=Column=Value [...]\n  wowcrucible dbc itemset inspect <ItemSet.dbc> <schema.xml> <set-id> [--spell=Spell.dbc]\n  wowcrucible dbc itemset clone <ItemSet.dbc> <schema.xml> <output.dbc> <source-set> <new-set> --map=old:new,... [--suffix=\" Variant\"]\n  wowcrucible dbc itemset effects <ItemSet.dbc> <schema.xml> <output.dbc> <set-id> --effect=required-items:spell-id [...]", code);
static int MpqHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json] [--listfile=paths.txt]\n  wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N] [--listfile=paths.txt]\n  wowcrucible mpq create <archive.mpq> <files/folders...>\n  wowcrucible mpq update <archive.mpq> <files/folders...>\n  wowcrucible mpq merge <output.mpq> <source-a.mpq> <source-b.mpq> [...] [--conflicts=block|earlier|later] [--listfile=paths.txt]", code);
static void PrintAnonymousMpqWarning(IReadOnlyList<MpqFileEntry> files, string? listFile)
{
    var anonymous = files.Count(file => ClientArchiveIndexService.IsAnonymous(file.ArchivePath));
    if (anonymous == 0) return;
    Console.Error.WriteLine($"WARNING: Archive opened successfully, but {anonymous:N0} file name(s) are unresolved StormLib placeholders.{(string.IsNullOrWhiteSpace(listFile) ? " Supply --listfile=paths.txt to recover known paths." : " The supplied listfile did not resolve every path.")}");
}
static int GroupHelp(string message, int code) { if (code == 0) Console.WriteLine(message); else Console.Error.WriteLine(message); return code; }

static int Help()
{
    Console.WriteLine("WoW Crucible CLI\n\nGlobal options:\n  --devbug   mirror terminal output and diagnostics to Logs\\Debug (newest 3 CLI sessions retained)\n\nCommand groups (run wowcrucible <group> --help for full syntax):\n  asset     inspect/preview models and build resumable extracted/PNG asset libraries\n  project   create portable content projects and reserve collision-checked IDs\n  client    install patches, clear cache, index/extract clients, and plan fusion\n  server    detect installed cores, audit DBC/SQL bindings, and stage client changes\n  db        inspect schemas, recover legacy SQL changes offline, audit items, and clone complete items\n  dbc       inspect/edit/validate/compare/promote DBCs and author item sets\n  mpq       list, extract, create, merge, and safely update small patch archives\n  manifest  define, verify, and build tiny reviewable patch MPQs\n\nExamples:\n  wowcrucible --devbug mpq list patch-H.MPQ\n  wowcrucible project --help\n  wowcrucible db --help\n  wowcrucible dbc --help\n  wowcrucible asset --help\n\nThe full copy-paste guide ships as docs\\CLI-REFERENCE.md beside the application.");
    return 0;
}

static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

static string? Option(IEnumerable<string> options, string prefix) => options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
static string FormatValues(IReadOnlyDictionary<string, object?> values) => values.Count == 0 ? "<missing>" : string.Join(", ", values.Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)}"));
static void PrintBinding(ServerTableBinding binding) => Console.Error.WriteLine($"Binding: {binding.DbcFileName} · {binding.Consumption} · SQL {binding.SqlTableName ?? "none"} · key {binding.KeyStrategy.Kind} · deploy {binding.Destinations} · restart {binding.Restart} · {binding.Profile}{(binding.SourceBacked ? " (source-backed)" : " (built-in profile)")} · {binding.SupportedRevision}");

static void PrintCompatibility(IEnumerable<PatchEntry> entries, string? requiredClientExecutableSha256)
{
    foreach (var issue in PatchManifestService.GetCompatibilityIssues(entries, requiredClientExecutableSha256))
        Console.Error.WriteLine($"{(issue.Code == "ProtectedGlueXmlUnbound" ? "WARNING" : "COMPAT")}: {issue.Message}");
}

static int PrintManifestValidation(PatchManifestValidationResult validation)
{
    foreach (var warning in validation.Warnings) Console.WriteLine($"WARN\t{warning.Code}\t{warning.ArchivePath}\t{warning.Message}");
    foreach (var error in validation.Errors) Console.WriteLine($"FAIL\t{error.Code}\t{error.ArchivePath}\t{error.Message}");
    Console.Error.WriteLine($"Manifest validation {(validation.Passed ? "passed" : "failed")}: {validation.Errors.Count:N0} error(s), {validation.Warnings.Count:N0} warning(s).");
    return validation.Passed ? 0 : 1;
}

static string SchemaRequirementMessage(string tableName, int fields, DbcSchemaResolution resolution) => resolution.MatchKind == DbcSchemaMatchKind.MissingTableFallback
    ? $"A matching named schema is required; '{tableName}' is absent from the selected schema."
    : $"A matching named schema is required; '{tableName}' defines {resolution.DefinedFieldCount} fields but the DBC contains {fields}.";

static bool DbcRowsEqual(WdbcFile left, int leftRow, WdbcFile right, int rightRow, IReadOnlyList<DbcColumn> columns)
{
    foreach (var column in columns)
    {
        if (column.Type == DbcValueType.StringOffset)
        {
            if (!left.GetString(left.GetRaw(leftRow, column)).Equals(right.GetString(right.GetRaw(rightRow, column)), StringComparison.Ordinal)) return false;
        }
        else if (left.GetRaw(leftRow, column) != right.GetRaw(rightRow, column)) return false;
    }
    return true;
}

static IReadOnlyDictionary<string, string> ParseSetOptions(IEnumerable<string> options)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var option in options)
    {
        if (!option.StartsWith("--set=", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Unknown row mutation option: {option}");
        var assignment = option[6..]; var separator = assignment.IndexOf('=');
        if (separator <= 0) throw new ArgumentException($"Invalid field assignment '{assignment}'. Expected Column=Value.");
        values[assignment[..separator]] = assignment[(separator + 1)..];
    }
    return values;
}

sealed class ConsoleProgress : IProgress<(int Done, int Total, string Path)>
{
    private readonly int _step;
    private int _lastPercentage = -1;
    public ConsoleProgress(int step) => _step = step;
    public void Report((int Done, int Total, string Path) value)
    {
        var percentage = value.Total == 0 ? 100 : value.Done * 100 / value.Total;
        if (value.Done != value.Total && percentage / _step == _lastPercentage / _step) return;
        _lastPercentage = percentage;
        Console.Error.WriteLine($"[{percentage,3}%] {value.Done:N0}/{value.Total:N0} · {value.Path}");
    }
}

sealed class ClientIndexConsoleProgress : IProgress<ClientIndexProgress>
{
    public void Report(ClientIndexProgress value) => Console.Error.WriteLine($"[{value.CompletedArchives:N0}/{value.TotalArchives:N0}] {value.Stage}{(value.Cached ? " (cached)" : string.Empty)} · {value.ArchivePath}");
}

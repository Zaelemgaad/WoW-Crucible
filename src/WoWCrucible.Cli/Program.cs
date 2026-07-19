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
        "project" => Project(commandArguments[1..], cancellation.Token).GetAwaiter().GetResult(),
        "tools" => Tooling(commandArguments[1..]),
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

static int Tooling(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ToolingHelp();
    if (args[0].Equals("commands", StringComparison.OrdinalIgnoreCase))
    {
        var commandOptions = args[1..]; var commandJson = commandOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var commandUnknown = commandOptions.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (commandUnknown.Length > 0) return Fail($"Unknown tools commands option: {commandUnknown[0]}");
        var query = string.Join(' ', commandOptions.Where(option => !option.StartsWith("--", StringComparison.Ordinal))); var matches = CrucibleCommandCatalog.Search(query, 100);
        if (commandJson) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Query = query, TotalCommands = CrucibleCommandCatalog.All.Count, Matches = matches.Select(match => new { match.Command.Id, match.Command.Title, match.Command.Category, match.Command.Description, match.Command.Aliases, match.Command.Shortcut, match.Score }) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else foreach (var match in matches) Console.WriteLine($"{match.Command.Id}\t{match.Command.Category}\t{match.Command.Title}\t{match.Command.Shortcut ?? "-"}\t{match.Command.Description}");
        return matches.Count > 0 ? 0 : 3;
    }
    if (!args[0].Equals("inventory", StringComparison.OrdinalIgnoreCase)) return Fail($"Unknown tools operation: {args[0]}");
    var options = args[1..]; var rootArgument = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var unassignedOnly = options.Any(option => option.Equals("--unassigned-only", StringComparison.OrdinalIgnoreCase)); var includeMissing = !options.Any(option => option.Equals("--no-missing", StringComparison.OrdinalIgnoreCase));
    var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--unassigned-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-missing", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown tools inventory option: {unknown[0]}");
    if (options.Count(option => !option.StartsWith("--", StringComparison.Ordinal)) > 1) return Fail("tools inventory accepts at most one workspace-root path.");
    var root = rootArgument is null ? ToolConsolidationInventoryService.FindWorkspaceRoot(CruciblePaths.ApplicationDirectory) : rootArgument; var report = ToolConsolidationInventoryService.Scan(root, includeMissing); var entries = unassignedOnly ? report.Entries.Where(entry => entry.Status == ToolInventoryStatus.Unassigned).ToArray() : report.Entries;
    if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { report.WorkspaceRoot, report.ScannedUtc, report.Tracked, report.Missing, report.Unassigned, Entries = entries }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    else
    {
        foreach (var entry in entries) Console.WriteLine($"{entry.Status}\t{entry.Scope}\t{entry.RelativePath}\t{entry.Capability}\t{entry.CrucibleDestination}");
        Console.Error.WriteLine($"Tool inventory: {report.Tracked:N0} tracked · {report.Unassigned:N0} NEW UNASSIGNED · {report.Missing:N0} expected root(s) absent.\nWorkspace: {report.WorkspaceRoot}");
    }
    return report.Unassigned == 0 ? 0 : 3;
}

static int Asset(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return AssetHelp();
    if (args is ["adt-texture-add-plan", var addTextureAdtPath, var newTexturePath, var addTextureCellText, var addTexturePlanPath, .. var addTextureOptions])
    {
        var overwrite = addTextureOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var encodingText = (Option(addTextureOptions, "--encoding=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal); var initialText = Option(addTextureOptions, "--initial-alpha=") ?? "0"; var unknown = addTextureOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--encoding=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--initial-alpha=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-add-plan option: {unknown[0]}");
        if (!Enum.TryParse<AdtNewLayerEncoding>(encodingText, true, out var encoding) || !Enum.IsDefined(encoding)) return Fail("--encoding must be auto, packed-4-bit, big-8-bit, or rle-8-bit."); if (!byte.TryParse(initialText, out var initialAlpha)) return Fail("--initial-alpha must be 0–255."); var cells = new List<(int, int)>();
        foreach (var token in addTextureCellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y."); cells.Add((x, y)); }
        var plan = AdtTextureStructureService.Plan(addTextureAdtPath, newTexturePath, cells, encoding, initialAlpha); AdtTextureStructureService.SavePlan(plan, addTexturePlanPath, overwrite); Console.Error.WriteLine($"Planned MTEX {plan.TextureId} ({plan.TexturePath}) plus one {plan.Encoding} layer in {plan.Cells.Count:N0} cell(s): {Path.GetFullPath(addTexturePlanPath)}"); return 0;
    }
    if (args is ["adt-texture-add-apply", var applyTextureStructurePlan, var textureStructureOutput, .. var textureStructureOptions])
    {
        var overwrite = textureStructureOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = textureStructureOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-add-apply option: {unknown[0]}"); var result = AdtTextureStructureService.Apply(AdtTextureStructureService.LoadPlan(applyTextureStructurePlan), textureStructureOutput, overwrite); Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nTextureId\t{result.TextureId}\nEditedCells\t{result.EditedCells:N0}"); return 0;
    }
    if (args is ["adt-alpha-info", var alphaAdtPath, .. var alphaInfoOptions])
    {
        var json = alphaInfoOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeCells = alphaInfoOptions.Contains("--cells", StringComparer.OrdinalIgnoreCase); var unknown = alphaInfoOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--cells", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-alpha-info option: {unknown[0]}"); var inspection = AdtAlphaMapService.Inspect(alphaAdtPath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"Path\t{inspection.Path}\nSHA256\t{inspection.Sha256}\nAlphaMaps\t{inspection.Maps.Count:N0}\nCellsWithAlpha\t{inspection.Maps.Select(map => (map.CellX, map.CellY)).Distinct().Count():N0}");
            foreach (var group in inspection.Maps.GroupBy(map => map.Encoding).OrderBy(group => group.Key)) Console.WriteLine($"ENCODING\t{group.Key}\t{group.Count():N0}");
            foreach (var finding in inspection.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (includeCells) foreach (var map in inspection.Maps) Console.WriteLine($"ALPHA\t{map.CellX},{map.CellY}\tslot={map.Slot}\ttexture={map.TextureId}\tpath={map.TexturePath ?? "MISSING"}\tencoding={map.Encoding}\tcapacity={map.Capacity}\tused={map.EncodedBytesUsed}\trange={map.Minimum}..{map.Maximum}\taverage={map.Average:0.###}");
        }
        return inspection.Findings.Count == 0 ? 0 : 3;
    }
    if (args is ["adt-alpha-plan", var planAlphaAdtPath, var alphaLayerText, var alphaCenterText, var alphaRadiusText, var targetAlphaText, var opacityText, var alphaCellText, var alphaPlanPath, .. var alphaPlanOptions])
    {
        var overwrite = alphaPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var falloffText = Option(alphaPlanOptions, "--falloff=") ?? "smooth"; var unknown = alphaPlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--falloff=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-alpha-plan option: {unknown[0]}");
        if (!int.TryParse(alphaLayerText, out var layerSlot) || layerSlot <= 0) return Fail("Alpha layer slot must be greater than zero; slot 0 is the opaque base layer."); var center = alphaCenterText.Split(':');
        if (center.Length != 2 || !float.TryParse(center[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerX) || !float.TryParse(center[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerY)) return Fail("Alpha-brush center must use tile-local center-x:center-y numbers.");
        if (!float.TryParse(alphaRadiusText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius) || !byte.TryParse(targetAlphaText, out var targetAlpha) || !float.TryParse(opacityText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var opacity)) return Fail("Radius and opacity must be numbers; target alpha must be 0–255.");
        if (!Enum.TryParse<AdtTerrainBrushFalloff>(falloffText, true, out var falloff) || !Enum.IsDefined(falloff)) return Fail("--falloff must be linear, smooth, or constant."); IReadOnlyList<(int X, int Y)>? cells = null;
        if (!alphaCellText.Equals("all", StringComparison.OrdinalIgnoreCase)) { var parsed = new List<(int, int)>(); foreach (var token in alphaCellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y or all."); parsed.Add((x, y)); } cells = parsed; }
        var plan = AdtAlphaMapService.Plan(planAlphaAdtPath, layerSlot, centerX, centerY, radius, targetAlpha, opacity, falloff, cells); AdtAlphaMapService.SavePlan(plan, alphaPlanPath, overwrite); Console.Error.WriteLine($"Planned {plan.Edits.Sum(edit => edit.ChangedPixels):N0} stored alpha-pixel edit(s) across {plan.Edits.Count:N0} map(s): {Path.GetFullPath(alphaPlanPath)}"); return 0;
    }
    if (args is ["adt-alpha-apply", var applyAlphaPlanPath, var alphaOutputPath, .. var alphaApplyOptions])
    {
        var overwrite = alphaApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = alphaApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-alpha-apply option: {unknown[0]}"); var result = AdtAlphaMapService.Apply(AdtAlphaMapService.LoadPlan(applyAlphaPlanPath), alphaOutputPath, overwrite); Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedMaps\t{result.EditedMaps:N0}\nEditedCells\t{result.EditedCells:N0}\nEditedPixels\t{result.EditedPixels:N0}"); return 0;
    }
    if (args is ["adt-texture-info", var textureAdtPath, .. var textureInfoOptions])
    {
        var json = textureInfoOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeCells = textureInfoOptions.Contains("--cells", StringComparer.OrdinalIgnoreCase); var unknown = textureInfoOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--cells", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-info option: {unknown[0]}"); var inspection = AdtTextureLayerService.Inspect(textureAdtPath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { Console.WriteLine($"Path\t{inspection.Path}\nSHA256\t{inspection.Sha256}\nTextures\t{inspection.Textures.Count:N0}\nLayers\t{inspection.Layers.Count:N0}\nCellsWithLayers\t{inspection.Layers.Select(layer => (layer.CellX, layer.CellY)).Distinct().Count():N0}"); foreach (var texture in inspection.Textures) Console.WriteLine($"MTEX\t{texture.Id}\t{texture.Path}"); foreach (var finding in inspection.Findings) Console.WriteLine($"FINDING\t{finding}"); if (includeCells) foreach (var layer in inspection.Layers) Console.WriteLine($"LAYER\t{layer.CellX},{layer.CellY}\tslot={layer.Slot}\ttexture={layer.TextureId}\tpath={layer.TexturePath ?? "MISSING"}\tflags=0x{layer.Flags:X}\talpha={layer.AlphaOffset}\teffect={layer.EffectId}"); } return inspection.Findings.Count == 0 ? 0 : 3;
    }
    if (args is ["adt-texture-plan", var planTextureAdtPath, var layerSlotText, var textureIdText, var textureCellText, var texturePlanPath, .. var texturePlanOptions])
    {
        var overwrite = texturePlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = texturePlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-plan option: {unknown[0]}"); if (!int.TryParse(layerSlotText, out var layerSlot) || layerSlot < 0 || !uint.TryParse(textureIdText, out var textureId)) return Fail("Layer slot must be non-negative and texture ID must be an unsigned MTEX index."); IReadOnlyList<(int X, int Y)> cells;
        if (textureCellText.Equals("all", StringComparison.OrdinalIgnoreCase)) cells = AdtTextureLayerService.Inspect(planTextureAdtPath).Layers.Where(layer => layer.Slot == layerSlot).Select(layer => (layer.CellX, layer.CellY)).Distinct().ToArray(); else { var parsed = new List<(int, int)>(); foreach (var token in textureCellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y or all."); parsed.Add((x, y)); } cells = parsed; }
        var plan = AdtTextureLayerService.Plan(planTextureAdtPath, cells, layerSlot, textureId); AdtTextureLayerService.SavePlan(plan, texturePlanPath, overwrite); Console.Error.WriteLine($"Planned {plan.Edits.Count:N0} MCLY layer edit(s) to MTEX {plan.TextureId} ({plan.TexturePath}): {Path.GetFullPath(texturePlanPath)}"); return 0;
    }
    if (args is ["adt-texture-apply", var applyTexturePlanPath, var textureOutputPath, .. var textureApplyOptions])
    {
        var overwrite = textureApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = textureApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-apply option: {unknown[0]}"); var result = AdtTextureLayerService.Apply(AdtTextureLayerService.LoadPlan(applyTexturePlanPath), textureOutputPath, overwrite); Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedLayers\t{result.EditedLayers:N0}\nEditedCells\t{result.EditedCells:N0}"); return 0;
    }
    if (args is ["adt-brush-plan", var brushAdtPath, var centerText, var radiusText, var strengthText, var brushPlanPath, .. var brushPlanOptions])
    {
        var overwrite = brushPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var falloffText = Option(brushPlanOptions, "--falloff=") ?? "smooth"; var modeText = (Option(brushPlanOptions, "--mode=") ?? "raise-lower").Replace("-", string.Empty, StringComparison.Ordinal); var targetText = Option(brushPlanOptions, "--target-height="); var seedText = Option(brushPlanOptions, "--seed=") ?? "0";
        var unknown = brushPlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--falloff=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--target-height=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-brush-plan option: {unknown[0]}");
        var center = centerText.Split(':'); if (center.Length != 2 || !float.TryParse(center[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerX) || !float.TryParse(center[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerY)) return Fail("Brush center must use tile-local center-x:center-y numbers.");
        if (!float.TryParse(radiusText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius) || !float.TryParse(strengthText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var strength)) return Fail("Brush radius and signed strength must be numbers.");
        if (!Enum.TryParse<AdtTerrainBrushFalloff>(falloffText, true, out var falloff) || !Enum.IsDefined(falloff)) return Fail("--falloff must be linear, smooth, or constant.");
        if (!Enum.TryParse<AdtTerrainBrushMode>(modeText, true, out var mode) || !Enum.IsDefined(mode)) return Fail("--mode must be raise-lower, flatten, smooth, or noise.");
        float? targetHeight = null; if (targetText is not null) { if (!float.TryParse(targetText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTarget) || !float.IsFinite(parsedTarget)) return Fail("--target-height must be finite."); targetHeight = parsedTarget; }
        if (!int.TryParse(seedText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seed)) return Fail("--seed must be a signed 32-bit integer.");
        var plan = AdtTerrainBrushService.Plan(brushAdtPath, centerX, centerY, radius, strength, falloff, mode, targetHeight, seed); AdtTerrainBrushService.SavePlan(plan, brushPlanPath, overwrite);
        Console.Error.WriteLine($"Planned {plan.Mode} with {plan.Vertices.Count:N0} MCVT vertex edit(s) across {plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct().Count():N0} cell(s): {Path.GetFullPath(brushPlanPath)}"); return 0;
    }
    if (args is ["adt-brush-apply", var applyBrushPlanPath, var brushOutputPath, .. var brushApplyOptions])
    {
        var overwrite = brushApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = brushApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-brush-apply option: {unknown[0]}");
        var result = AdtTerrainBrushService.Apply(AdtTerrainBrushService.LoadPlan(applyBrushPlanPath), brushOutputPath, overwrite);
        Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedVertices\t{result.EditedVertices:N0}\nEditedCells\t{result.EditedCells:N0}"); return 0;
    }
    if (args is ["adt-height-plan", var adtPath, var deltaText, var cellText, var heightPlanPath, .. var heightPlanOptions])
    {
        var overwrite = heightPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = heightPlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-height-plan option: {unknown[0]}");
        if (!float.TryParse(deltaText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delta) || !float.IsFinite(delta)) return Fail("Height delta must be finite.");
        IReadOnlyList<(int X, int Y)> cells;
        if (cellText.Equals("all", StringComparison.OrdinalIgnoreCase)) cells = MapAssetInspectionService.Inspect(adtPath).Cells.Where(cell => cell.Present).Select(cell => (cell.X, cell.Y)).ToArray();
        else
        {
            var parsed = new List<(int, int)>(); foreach (var token in cellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y or all."); parsed.Add((x, y)); } cells = parsed;
        }
        var plan = AdtHeightEditService.Plan(adtPath, cells, delta); AdtHeightEditService.SavePlan(plan, heightPlanPath, overwrite);
        Console.Error.WriteLine($"Planned {plan.Cells.Count:N0} ADT terrain-cell edit(s) at delta {plan.Delta:R}: {Path.GetFullPath(heightPlanPath)}"); return 0;
    }
    if (args is ["adt-height-apply", var applyHeightPlanPath, var heightOutputPath, .. var heightApplyOptions])
    {
        var overwrite = heightApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = heightApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-height-apply option: {unknown[0]}");
        var result = AdtHeightEditService.Apply(AdtHeightEditService.LoadPlan(applyHeightPlanPath), heightOutputPath, overwrite);
        Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedCells\t{result.EditedCells:N0}\nDelta\t{result.Delta:R}"); return 0;
    }
    if (args is ["map-info", var mapPath, .. var mapOptions])
    {
        var json = mapOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeCells = mapOptions.Contains("--cells", StringComparer.OrdinalIgnoreCase); var includePlacements = mapOptions.Contains("--placements", StringComparer.OrdinalIgnoreCase);
        var unknown = mapOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--cells", StringComparison.OrdinalIgnoreCase) && !option.Equals("--placements", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset map-info option: {unknown[0]}");
        var inspection = MapAssetInspectionService.Inspect(mapPath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"Path\t{inspection.Path}\nKind\t{inspection.Kind}\nVersion\t{inspection.Version}\nGrid\t{inspection.GridWidth}x{inspection.GridHeight}\nPresent\t{inspection.PresentCells:N0}/{inspection.Cells.Count:N0}\nWorldTile\t{inspection.TileX?.ToString() ?? "-"},{inspection.TileY?.ToString() ?? "-"}\nHeight\t{inspection.MinimumHeight?.ToString("R") ?? "-"}..{inspection.MaximumHeight?.ToString("R") ?? "-"}\nTextures\t{inspection.TexturePaths.Count:N0}\nModels\t{inspection.ModelPaths.Count:N0}\nM2Placements\t{inspection.M2Placements.Count:N0}\nWMOs\t{inspection.WmoPaths.Count:N0}\nWMOPlacements\t{inspection.WmoPlacements.Count:N0}\nHeaderFlags\t0x{inspection.HeaderFlags:X}");
            foreach (var chunk in inspection.Chunks) Console.WriteLine($"CHUNK\t{chunk.Id}\tcount={chunk.Occurrences:N0}\tbytes={chunk.PayloadBytes:N0}");
            foreach (var finding in inspection.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (includeCells) foreach (var cell in inspection.Cells.Where(cell => cell.Present)) Console.WriteLine($"CELL\t{cell.X},{cell.Y}\tflags=0x{cell.Flags:X}\tarea={cell.AreaId?.ToString() ?? "-"}\tholes=0x{cell.Holes?.ToString("X") ?? "-"}\theight={cell.MinimumHeight?.ToString("R") ?? "-"}..{cell.MaximumHeight?.ToString("R") ?? "-"}");
            if (includePlacements) foreach (var placement in inspection.M2Placements) Console.WriteLine($"M2_PLACEMENT\tindex={placement.Index:N0}\tuid={placement.UniqueId:N0}\tname={placement.NameId:N0}\tpath={placement.ClientPath ?? "<unresolved>"}\tposition={placement.Position.X:R},{placement.Position.Y:R},{placement.Position.Z:R}\torientation={placement.Orientation.X:R},{placement.Orientation.Y:R},{placement.Orientation.Z:R}\tflags=0x{placement.Flags:X}\tscaleRaw={placement.ScaleRaw:N0}\tscale={placement.Scale:R}");
            if (includePlacements) foreach (var placement in inspection.WmoPlacements) Console.WriteLine($"WMO_PLACEMENT\tindex={placement.Index:N0}\tuid={placement.UniqueId:N0}\tname={placement.NameId:N0}\tpath={placement.ClientPath ?? "<unresolved>"}\tposition={placement.Position.X:R},{placement.Position.Y:R},{placement.Position.Z:R}\torientation={placement.Orientation.X:R},{placement.Orientation.Y:R},{placement.Orientation.Z:R}\textents={placement.MinimumExtent.X:R},{placement.MinimumExtent.Y:R},{placement.MinimumExtent.Z:R}..{placement.MaximumExtent.X:R},{placement.MaximumExtent.Y:R},{placement.MaximumExtent.Z:R}\tflags=0x{placement.Flags:X}\tdoodadSet={placement.DoodadSet:N0}\tnameSet={placement.NameSet:N0}\tscaleRaw={placement.ScaleRaw:N0}\tscale={placement.Scale:R}");
        }
        return inspection.Findings.Any(finding => finding.StartsWith("MVER", StringComparison.Ordinal)) ? 3 : 0;
    }
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
    if (args is ["dependency-graph", var dependencyLibrary, var dependencyRoot, .. var dependencyOptions])
    {
        var json = dependencyOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var onlyProblems = dependencyOptions.Contains("--only-problems", StringComparer.OrdinalIgnoreCase); var manifestPath = Option(dependencyOptions, "--manifest="); var outputMpq = Option(dependencyOptions, "--output-mpq=") ?? "patch-Crucible-Assets.MPQ"; var targetIndexPath = Option(dependencyOptions, "--target-index=");
        var unknown = dependencyOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--only-problems", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--manifest=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output-mpq=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--target-index=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--target-choice=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown dependency-graph option: {unknown[0]}");
        var targetChoices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in dependencyOptions.Where(option => option.StartsWith("--target-choice=", StringComparison.OrdinalIgnoreCase)))
        {
            var value = option["--target-choice=".Length..]; var separator = value.IndexOf('|'); if (separator <= 0 || separator == value.Length - 1) return Fail("--target-choice requires <client-path>|<archive-relative-path>.");
            targetChoices[PatchInputMapper.NormalizeArchivePath(value[..separator])] = PatchInputMapper.NormalizeArchivePath(value[(separator + 1)..]);
        }
        if (targetChoices.Count > 0 && targetIndexPath is null) return Fail("--target-choice requires --target-index.");
        var index = AssetComparisonService.BuildIndex(dependencyLibrary); var location = ClientAssetDependencyService.InferLocation(index, dependencyRoot); var target = targetIndexPath is null ? null : ClientEffectiveAssetCatalog.Load(targetIndexPath); var graph = ClientAssetDependencyService.Analyze(index, location, null, target, targetChoices);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(graph, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"ROOT\t{graph.Root.ClientPath}\t{graph.Root.Provenance}\t{graph.Root.SourcePath}\nNODES\t{graph.Nodes.Count}\nPATCH_FILES\t{graph.PatchEntries.Count}\nINHERITED_TARGET\t{graph.Inherited.Count}\nEXTERNAL_BINDINGS\t{graph.ExternalBindings.Count}\nBLOCKING\t{graph.Blocking.Count}");
            foreach (var node in graph.Nodes.Where(node => !onlyProblems || node.State is ClientAssetDependencyState.Missing or ClientAssetDependencyState.CrossSourceConflict or ClientAssetDependencyState.TargetAmbiguous or ClientAssetDependencyState.Invalid))
                Console.WriteLine($"{node.State.ToString().ToUpperInvariant()}\tdepth={node.Depth}\t{node.Kind}\t{node.ClientPath}\t{node.SourcePath ?? "-"}\t{node.Message}");
        }
        if (manifestPath is not null)
        {
            if (graph.Blocking.Count > 0) { Console.Error.WriteLine($"BLOCKED: Dependency closure has {graph.Blocking.Count:N0} blocking node(s); no manifest was written."); return 3; }
            if (graph.PatchEntries.Count == 0) { Console.Error.WriteLine($"NO PATCH NEEDED: all {graph.Inherited.Count:N0} dependency node(s) are supplied by the selected target client; no empty manifest was written."); return 0; }
            PatchManifestService.Save(manifestPath, Path.GetFileNameWithoutExtension(manifestPath), outputMpq, graph.PatchEntries, policy: new(ExpectedEntryCount: graph.PatchEntries.Count), targetClient: graph.TargetRequirement); Console.Error.WriteLine($"Wrote dependency-complete patch manifest with {graph.PatchEntries.Count:N0} file(s){(graph.TargetRequirement is null ? string.Empty : $", bound to target fingerprint {graph.TargetRequirement.IndexFingerprint} with {graph.TargetRequirement.InheritedAssets.Count:N0} inherited path(s)")}: {Path.GetFullPath(manifestPath)}");
        }
        return graph.Blocking.Count == 0 ? 0 : 3;
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
    if (args is ["wmo-preview-info", var wmoPath, .. var wmoOptions])
    {
        var json = wmoOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeGroups = wmoOptions.Contains("--groups", StringComparer.OrdinalIgnoreCase); var contentRoot = Option(wmoOptions, "--content-root=");
        var unknown = wmoOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--groups", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--content-root=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown wmo-preview-info option: {unknown[0]}");
        var geometry = WmoPreviewGeometryService.Load(wmoPath); var textures = WmoPreviewGeometryService.ResolveTextureFiles(geometry, contentRoot);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Geometry = geometry, ResolvedTextureFiles = textures }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        else
        {
            Console.WriteLine($"Root\t{geometry.RootPath}\nVersion\t{geometry.Version}\nGroups\t{geometry.Groups.Count:N0}\nVertices\t{geometry.Vertices.Count:N0}\nTriangles\t{geometry.TriangleIndices.Count / 3:N0}\nMaterials\t{geometry.Materials.Count:N0}\nResolvedTextures\t{textures.Count:N0}\nMinimum\t{geometry.Minimum}\nMaximum\t{geometry.Maximum}");
            foreach (var material in geometry.Materials) Console.WriteLine($"MATERIAL\t{material.Index}\tshader={material.Shader}\tblend={material.BlendMode}\tflags=0x{material.Flags:X}\ttexture={material.Texture1 ?? "<none>"}\tresolved={textures.GetValueOrDefault(material.Index, "<missing>")}");
            foreach (var finding in geometry.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (includeGroups) foreach (var group in geometry.Groups) { Console.WriteLine($"GROUP\t{group.Index:000}\tvertices={group.VertexCount:N0}\ttriangles={group.TriangleIndexCount / 3:N0}\tbatches={group.BatchCount:N0}\tflags=0x{group.Flags:X}\t{group.Path}"); foreach (var finding in group.Findings) Console.WriteLine($"GROUP_FINDING\t{group.Index:000}\t{finding}"); }
        }
        return geometry.Findings.Count == 0 ? 0 : 3;
    }
    if (args is ["path-candidates", var libraryPath, var clientPath, .. var candidateOptions])
    {
        var json = candidateOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var preferred = Option(candidateOptions, "--preferred=");
        var unknown = candidateOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--preferred=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown path-candidates option: {unknown[0]}");
        var index = ClientAssetDependencyService.OpenLibraryLayout(libraryPath); var candidates = ClientAssetDependencyService.FindCandidates(index, clientPath);
        var selected = !string.IsNullOrWhiteSpace(preferred)
            ? candidates.SingleOrDefault(value => value.Provenance.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            : candidates.Count == 1 ? candidates[0] : null;
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Library = index.LibraryRoot, ClientPath = PatchInputMapper.NormalizeArchivePath(clientPath), Preferred = preferred, Selected = selected, Candidates = candidates, RequiresExplicitChoice = selected is null && candidates.Count > 1 }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"ClientPath\t{PatchInputMapper.NormalizeArchivePath(clientPath)}\nCandidates\t{candidates.Count:N0}\nSelected\t{selected?.Provenance ?? "<none>"}");
            foreach (var candidate in candidates) Console.WriteLine($"CANDIDATE\t{candidate.Provenance}\t{candidate.SourcePath}");
            if (candidates.Count > 1 && selected is null) Console.WriteLine("FINDING\tMultiple provenance layers exist; use --preferred=<exact provenance> to select one explicitly.");
            else if (!string.IsNullOrWhiteSpace(preferred) && selected is null) Console.WriteLine($"FINDING\tPreferred provenance was not found: {preferred}");
        }
        return selected is not null ? 0 : 3;
    }
    if (args is ["model-export", var exportModelPath, var exportObjPath, .. var exportOptions])
    {
        var overwrite = exportOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var allGeosets = exportOptions.Contains("--all-geosets", StringComparer.OrdinalIgnoreCase); var naked = exportOptions.Contains("--naked", StringComparer.OrdinalIgnoreCase);
        var skinPath = Option(exportOptions, "--skin="); var animationText = Option(exportOptions, "--animation="); var timeText = Option(exportOptions, "--time="); var groupText = Option(exportOptions, "--groups=");
        var textureOptions = exportOptions.Where(option => option.StartsWith("--texture=", StringComparison.OrdinalIgnoreCase)).ToArray();
        var unknown = exportOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--all-geosets", StringComparison.OrdinalIgnoreCase) && !option.Equals("--naked", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--skin=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--animation=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--time=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--groups=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--texture=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown model-export option: {unknown[0]}");
        if (allGeosets && (naked || groupText is not null)) return Fail("--all-geosets cannot be combined with --naked or --groups.");
        if (timeText is not null && animationText is null) return Fail("--time requires --animation=<sequence-index>.");
        var selectedGroups = naked ? new Dictionary<int, int>(M2GeosetCatalog.NakedCharacterSelection) : new Dictionary<int, int>();
        if (groupText is not null)
        {
            foreach (var token in groupText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = token.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var group) || !int.TryParse(parts[1], out var variant) || group < 0 || variant < 0 || variant > 99) return Fail($"Invalid geoset selection '{token}'; use group:variant with non-negative values and variants 0-99.");
                selectedGroups[group] = variant;
            }
        }
        var selection = selectedGroups.Count == 0 ? null : new M2GeosetSelection(selectedGroups, naked ? "naked CLI export preset" : "explicit CLI export selection");
        var geometry = M2PreviewGeometryService.Load(exportModelPath, skinPath, allGeosets ? M2PreviewVisibilityMode.AllGeosets : M2PreviewVisibilityMode.BaseAppearance, selection);
        M2AnimationPose? pose = null;
        if (animationText is not null)
        {
            if (!int.TryParse(animationText, out var sequence) || sequence < 0 || sequence >= geometry.Sequences.Count) return Fail($"--animation must be a sequence index from 0 through {Math.Max(0, geometry.Sequences.Count - 1)}.");
            if (timeText is not null && (!double.TryParse(timeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTime) || !double.IsFinite(parsedTime))) return Fail("--time must be a finite millisecond value.");
            var time = timeText is null ? 0d : double.Parse(timeText, System.Globalization.CultureInfo.InvariantCulture); pose = M2AnimationService.CreatePose(geometry); M2AnimationService.SampleInto(geometry, sequence, time, pose);
        }
        var textures = new Dictionary<int, RgbaTexture>();
        foreach (var option in textureOptions)
        {
            var value = option["--texture=".Length..]; var separator = value.IndexOf(':');
            if (separator <= 0 || !int.TryParse(value[..separator], out var slot) || slot < 0 || separator == value.Length - 1) return Fail($"Invalid texture binding '{value}'; use --texture=slot:path.blp.");
            textures[slot] = BlpTextureService.Decode(value[(separator + 1)..]);
        }
        var result = M2ObjExportService.Export(geometry, exportObjPath, pose, textures, overwrite);
        Console.WriteLine($"OBJ\t{result.ObjPath}\nMTL\t{result.MaterialPath}\nReceipt\t{result.ReceiptPath}\nVertices\t{result.Vertices:N0}\nTriangles\t{result.Triangles:N0}\nPosed\t{result.Posed}\nTextures\t{result.TexturePaths.Count:N0}");
        foreach (var texture in result.TexturePaths) Console.WriteLine($"TEXTURE\t{texture}");
        return 0;
    }
    if (args is ["preview-info", var previewModelPath, .. var previewOptions])
    {
        var known = previewOptions.Where(option => option.Equals("--all-geosets", StringComparison.OrdinalIgnoreCase) || option.Equals("--naked", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--groups=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--hair=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--facial-hair=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--animation=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--time=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (known.Length != previewOptions.Length) return Fail($"Unknown preview-info option: {previewOptions.Except(known).First()}");
        var allGeosets = previewOptions.Contains("--all-geosets", StringComparer.OrdinalIgnoreCase); var naked = previewOptions.Contains("--naked", StringComparer.OrdinalIgnoreCase); var groupText = Option(previewOptions, "--groups=");
        if (allGeosets && (naked || groupText is not null)) return Fail("--all-geosets cannot be combined with --naked or --groups because it intentionally shows every variant.");
        var mode = allGeosets ? M2PreviewVisibilityMode.AllGeosets : M2PreviewVisibilityMode.BaseAppearance;
        var dbcFolder = Option(previewOptions, "--dbc="); var hairText = Option(previewOptions, "--hair="); var facialText = Option(previewOptions, "--facial-hair=");
        if (naked && (hairText is not null || facialText is not null)) return Fail("--naked cannot be combined with --hair or --facial-hair.");
        var selectedGroups = naked ? new Dictionary<int, int>(M2GeosetCatalog.NakedCharacterSelection) : new Dictionary<int, int>(); var selectionSource = naked ? "naked character preset" : string.Empty;
        if (hairText is not null || facialText is not null)
        {
            if (dbcFolder is null) return Fail("--hair and --facial-hair require --dbc=<folder> so Crucible can resolve exact build-12340 geosets.");
            var identity = CharacterAppearanceService.Infer(Path.GetDirectoryName(Path.GetFullPath(previewModelPath)) ?? string.Empty, Path.GetFileName(previewModelPath))
                ?? throw new InvalidDataException("The model path/name does not identify a supported playable race and sex.");
            var plan = CharacterAppearanceService.ResolveGeosets(dbcFolder, identity, ParseVariation(hairText), ParseVariation(facialText));
            foreach (var pair in plan.GroupVariants) selectedGroups[pair.Key] = pair.Value; selectionSource = "CharHairGeosets.dbc + CharacterFacialHairStyles.dbc";
            foreach (var warning in plan.Warnings) Console.Error.WriteLine($"WARNING: {warning}");
        }
        if (groupText is not null)
        {
            foreach (var pair in ParseGroups(groupText)) selectedGroups[pair.Key] = pair.Value;
            selectionSource = selectionSource.Length == 0 ? "explicit CLI group selection" : selectionSource + " + explicit CLI overrides";
        }
        var selection = selectedGroups.Count == 0 ? null : new M2GeosetSelection(selectedGroups, selectionSource);
        var geometry = M2PreviewGeometryService.Load(previewModelPath, visibilityMode: mode, geosetSelection: selection);
        Console.WriteLine($"Model\t{geometry.ModelPath}\nSkin\t{geometry.SkinPath}\nVertices\t{geometry.Vertices.Count:N0}\nBones\t{geometry.Bones.Count:N0}\nAttachments\t{geometry.Attachments.Count:N0}\nGeosets\t{geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} ({geometry.VisibilityMode})\nTriangles\t{geometry.TriangleIndices.Count / 3:N0}/{geometry.TotalTriangleIndices / 3:N0}\nMinimum\t{geometry.Minimum}\nMaximum\t{geometry.Maximum}");
        if (geometry.GeosetSelection is not null) Console.WriteLine($"GEOSET_SELECTION\t{geometry.GeosetSelection.Source}\t{string.Join(",", geometry.GeosetSelection.GroupVariants.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}");
        foreach (var group in M2GeosetCatalog.Describe(geometry.Submeshes)) Console.WriteLine($"GEOSET_GROUP\t{group.Group}\t{group.Name}\tvariants={string.Join(',', group.Variants.Select(variant => variant.Variant))}\tvisible={string.Join(',', group.Variants.Where(variant => variant.Visible).Select(variant => variant.Variant))}");
        foreach (var section in geometry.Submeshes) Console.WriteLine($"SUBMESH\t{section.Index}\tgeoset={section.GeosetId}\tgroup={section.GeosetGroup}:{section.GeosetGroupName}\tvariant={section.GeosetVariant}\tvisible={section.Visible}\ttriangles={section.TriangleIndexCount / 3}");
        foreach (var slot in geometry.TextureSlots) Console.WriteLine($"TEXTURE\t{slot.Index}\t{slot.Type}\t{slot.Flags}\t{slot.EmbeddedPath ?? "<external appearance binding>"}");
        foreach (var renderFlag in geometry.RenderFlags) Console.WriteLine($"RENDER_FLAG\t{renderFlag.Index}\tflags=0x{renderFlag.Flags:X}\tblend={renderFlag.BlendMode}\tunlit={renderFlag.Unlit}\ttwo-sided={renderFlag.TwoSided}");
        foreach (var material in geometry.MaterialUnits) Console.WriteLine($"MATERIAL\t{material.Index}\tsubmesh={material.SubmeshIndex}\tshader={material.ShaderId}\trender-flag={material.RenderFlagsIndex}\tlookup={material.TextureLookupIndex}\ttexture={(material.TextureDefinitionIndex < 0 ? "<unresolved>" : material.TextureDefinitionIndex)}\tpasses={material.TextureCount}");
        foreach (var batch in geometry.Batches) Console.WriteLine($"BATCH\t{submeshLabel(batch)}\tindices={batch.TriangleStart}+{batch.TriangleIndexCount}\tmaterial={batch.MaterialUnitIndex?.ToString() ?? "<none>"}\ttexture={batch.TextureDefinitionIndex?.ToString() ?? "<none>"}\tblend={batch.BlendMode}\tflags=0x{batch.RenderFlags:X}");
        foreach (var attachment in geometry.Attachments) Console.WriteLine($"ATTACHMENT\trecord={attachment.Index}\tid={attachment.Id}\t{attachment.Name}\tbone={attachment.BoneIndex}\tposition={attachment.Position.X:R},{attachment.Position.Y:R},{attachment.Position.Z:R}\tlookup={(attachment.LookupSlots.Count == 0 ? "<none>" : string.Join(',', attachment.LookupSlots))}");
        foreach (var sequence in geometry.Sequences) Console.WriteLine($"SEQUENCE\tindex={sequence.Index}\tid={sequence.AnimationId}:{sequence.SubAnimationId}\tduration={sequence.DurationMilliseconds}\tflags=0x{sequence.Flags:X}\talias={(sequence.IsAlias ? sequence.AliasSequence : "<none>")}");
        var animationText = Option(previewOptions, "--animation="); var timeText = Option(previewOptions, "--time=");
        if (timeText is not null && animationText is null) return Fail("--time requires --animation=<sequence-index>.");
        if (animationText is not null)
        {
            if (!int.TryParse(animationText, out var animationIndex) || animationIndex < 0 || animationIndex >= geometry.Sequences.Count) return Fail($"--animation must be a sequence index from 0 through {Math.Max(0, geometry.Sequences.Count - 1)}.");
            if (timeText is not null && (!double.TryParse(timeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTime) || !double.IsFinite(parsedTime))) return Fail("--time must be a finite millisecond value.");
            var time = timeText is null ? 0d : double.Parse(timeText, System.Globalization.CultureInfo.InvariantCulture); var pose = M2AnimationService.CreatePose(geometry);
            M2AnimationService.SampleInto(geometry, animationIndex, time, pose);
            Console.WriteLine($"POSE\tsequence={animationIndex}\ttime={pose.TimeMilliseconds:R}\tminimum={pose.Minimum.X:R},{pose.Minimum.Y:R},{pose.Minimum.Z:R}\tmaximum={pose.Maximum.X:R},{pose.Maximum.Y:R},{pose.Maximum.Z:R}\tvertices={pose.Vertices.Length}\tbones={pose.BoneTransforms.Length}");
        }
        return 0;

        static string submeshLabel(M2PreviewBatch batch) => $"submesh={batch.SubmeshIndex},geoset={batch.GeosetId}";
        static uint? ParseVariation(string? value) => value is null ? null : uint.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"Invalid non-negative appearance variation: {value}");
        static IReadOnlyDictionary<int, int> ParseGroups(string value)
        {
            var result = new Dictionary<int, int>();
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = token.Split(':', StringSplitOptions.TrimEntries); if (parts.Length != 2 || !int.TryParse(parts[0], out var group) || !int.TryParse(parts[1], out var variant) || group is < 0 or > 655 || variant is < 0 or > 99)
                    throw new ArgumentException($"Invalid geoset group selection '{token}'. Use group:variant with group 0..655 and variant 0..99; variant 0 hides that group.");
                result[group] = variant;
            }
            if (result.Count == 0) throw new ArgumentException("--groups requires at least one group:variant selection.");
            return result;
        }
    }
    if (args is ["appearance-info", var charSectionsPath, var logicalPath, var modelFile])
    {
        var identity = CharacterAppearanceService.Infer(logicalPath, modelFile) ?? throw new InvalidDataException("The logical path/model name does not identify a supported playable race and sex.");
        var skins = CharacterAppearanceService.LoadBaseSkins(charSectionsPath, identity);
        Console.WriteLine($"Character\t{identity.RaceName}\t{identity.SexName}\tRaceID={identity.RaceId}\tSexID={identity.SexId}\nBaseSkins\t{skins.Count:N0}");
        foreach (var skin in skins) Console.WriteLine($"SKIN\t{skin.Id}\tvariation={skin.VariationIndex}\tcolor={skin.ColorIndex}\tflags=0x{skin.Flags:X}\t{skin.TexturePath}");
        return 0;
    }
    if (args is ["appearance-render", var appearanceLibrary, var appearanceDbcFolder, var appearanceLogicalPath, var appearanceModelFile, var appearanceOutput, .. var appearanceOptions])
    {
        var skinId = UIntOption(appearanceOptions,"--skin="); var faceId = UIntOption(appearanceOptions,"--face="); var facialId = UIntOption(appearanceOptions,"--facial-hair="); var hairId = UIntOption(appearanceOptions,"--hair=");
        var requestedSource=Option(appearanceOptions,"--source=");var hairOutput=Option(appearanceOptions,"--hair-output=");
        var unknown=appearanceOptions.Where(option=>!option.StartsWith("--skin=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--face=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--facial-hair=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--hair=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--source=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--hair-output=",StringComparison.OrdinalIgnoreCase)&&!option.Equals("--overwrite",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)throw new ArgumentException($"Unknown appearance-render option: {unknown[0]}");
        var identity=CharacterAppearanceService.Infer(appearanceLogicalPath,appearanceModelFile)??throw new InvalidDataException("The logical path/model name does not identify a supported playable race and sex.");var index=AssetComparisonService.BuildIndex(appearanceLibrary);
        var plan=CharacterAppearancePreviewService.Build(index,appearanceDbcFolder,identity,skinId,faceId,facialId,hairId);if(requestedSource is not null){var source=plan.Sources.FirstOrDefault(item=>item.Provenance.Equals(requestedSource,StringComparison.OrdinalIgnoreCase)||item.FullPath.Equals(requestedSource,StringComparison.OrdinalIgnoreCase))??throw new KeyNotFoundException($"Appearance source '{requestedSource}' was not found. Available: {string.Join(", ",plan.Sources.Select(item=>item.Provenance))}");plan=CharacterAppearancePreviewService.Build(index,appearanceDbcFolder,identity,skinId,faceId,facialId,hairId,source.FullPath);}
        if(plan.SelectedSource is null)throw new InvalidOperationException($"Choose --source from: {string.Join(", ",plan.Sources.Select(item=>item.Provenance))}");var composed=CharacterAppearancePreviewService.Compose(index,plan);var overwrite=appearanceOptions.Contains("--overwrite",StringComparer.OrdinalIgnoreCase);BlpTextureService.WritePng(appearanceOutput,composed.Body,overwrite);if(hairOutput is not null&&composed.Hair is not null)BlpTextureService.WritePng(hairOutput,composed.Hair,overwrite);
        Console.WriteLine($"CHARACTER\t{identity.RaceName} {identity.SexName}\nSOURCE\t{plan.SelectedSource.Provenance}\nBODY\t{Path.GetFullPath(appearanceOutput)}\nHAIR\t{(hairOutput is null||composed.Hair is null?"not written":Path.GetFullPath(hairOutput))}\nMISSING\t{string.Join(",",composed.Missing)}\nGEOSETS\t{string.Join(",",plan.Geosets.GroupVariants.Select(pair=>$"{pair.Key}:{pair.Value}"))}");return 0;

        static uint? UIntOption(string[] options,string prefix){var value=Option(options,prefix);if(value is null)return null;return uint.TryParse(value,out var parsed)?parsed:throw new ArgumentException($"{prefix.TrimEnd('=')} requires an unsigned integer.");}
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

static int AssetHelp(int code = 0) => GroupHelp("""
Usage:
  wowcrucible asset texture-info <file.blp>
  wowcrucible asset texture-decode <file.blp> <output.png> [--mip=N] [--overwrite]
  wowcrucible asset texture-encode <image.png|jpg|bmp|tga> <output.blp> [--format=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--overwrite]
  wowcrucible asset texture-validate <file-or-folder> [--recursive]
  wowcrucible asset inspect <model.m2|building.wmo>...
  wowcrucible asset dependency-graph <processed-library> <root.m2|wmo|adt|wdt> [--target-index=client-index] [--target-choice=client-path|archive]... [--only-problems] [--manifest=patch.json] [--output-mpq=name.MPQ] [--format=text|json]
  wowcrucible asset preview-info <wrath-model.m2> [--dbc=folder] [--hair=N] [--facial-hair=N] [--animation=sequence-index] [--time=milliseconds] [--naked|--groups=group:variant,...|--all-geosets]
  wowcrucible asset model-export <wrath-model.m2> <output.obj> [--skin=file.skin] [--animation=sequence-index --time=milliseconds] [--texture=slot:file.blp]... [--naked|--groups=group:variant,...|--all-geosets] [--overwrite]
  wowcrucible asset wmo-preview-info <root-or-group.wmo> [--groups] [--content-root=folder] [--format=text|json]
  wowcrucible asset path-candidates <processed-library> <client-path> [--preferred=provenance] [--format=text|json]
  wowcrucible asset appearance-info <CharSections.dbc> <logical-path> <model-file>
  wowcrucible asset appearance-render <library> <dbc-folder> <logical-path> <model-file> <body.png> [--skin=N --face=N --facial-hair=N --hair=N --source=name --hair-output=file] [--overwrite]
  wowcrucible asset appearance-compose <base.blp> <output.png> [component options] [--overwrite]
  wowcrucible asset models <library-folder> <logical-directory>
  wowcrucible asset definitive-status <library-folder>
  wowcrucible asset definitive-stage <library-folder> <output-folder>
  wowcrucible asset workspace <new-output-folder> <files/folders...>
  wowcrucible asset library-plan <source-folder> <library-folder> [--max-gb=2]
  wowcrucible asset library-run <library-folder> [--workers=6]
  wowcrucible asset library-import <extracted-folder> <library-folder> <provenance> [--workers=6]
  wowcrucible asset library-repair <library-folder> [--workers=6]
  wowcrucible asset library-artifacts <library-folder> [--source-root=folder]... [--apply]
  wowcrucible asset library-layout <library-folder> [--apply]
  wowcrucible asset library-consolidate <library-folder> [--apply]
  wowcrucible asset library-catalog <library-folder>
  wowcrucible asset library-status <library-folder>
  wowcrucible asset compare-folders <library-folder> [path-filter]
  wowcrucible asset compare-files <library-folder> <logical-directory>

Full guide: docs/CLI-REFERENCE.md
""", code);

static async Task<int> Project(string[] args, CancellationToken cancellationToken)
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
    var liveOperation = args.FirstOrDefault()?.ToLowerInvariant();
    var reserveLive = liveOperation == "reserve-live";
    var connectionOffset = reserveLive ? 4 : 2;
    if (liveOperation is "occupancy" or "reserve-live" && args.Length >= connectionOffset + 5 &&
        Enum.TryParse<ContentIdDomain>(args[reserveLive ? 2 : 1], true, out var liveDomain))
    {
        var liveCount = 0;
        if (reserveLive && (!int.TryParse(args[3], out liveCount) || liveCount < 1)) return Fail("reserve-live count must be a positive integer.");
        var host = args[connectionOffset]; var portText = args[connectionOffset + 1]; var user = args[connectionOffset + 2]; var database = args[connectionOffset + 3];
        if (!uint.TryParse(portText, out var port) || port is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
        var options = args[(connectionOffset + 4)..]; var dbc = Option(options, "--dbc="); var schema = Option(options, "--schema="); var startText = Option(options, "--start="); var purpose = Option(options, "--purpose=") ?? "Unspecified content"; var json = options.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var passwordEnvironment = Option(options, "--password-env=") ?? "WOW_CRUCIBLE_DB_PASSWORD"; var sslText = Option(options, "--ssl=") ?? "Preferred";
        var allowed = new[] { "--dbc=", "--schema=", "--start=", "--purpose=", "--password-env=", "--ssl=" };
        var unknown = options.Where(option => !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown project {liveOperation} option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(passwordEnvironment)) return Fail("--password-env requires a non-empty environment-variable name.");
        var password = Environment.GetEnvironmentVariable(passwordEnvironment); if (password is null) return Fail($"Set the {passwordEnvironment} environment variable for this process. Passwords are not accepted on the command line.");
        if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(sslText, true, out var ssl)) return Fail($"Unknown SSL mode: {sslText}");
        uint? start = null; if (startText is not null) { if (!uint.TryParse(startText, out var parsedStart)) return Fail("--start must be an unsigned integer."); start = parsedStart; }
        var profile = new DatabaseConnectionProfile(host, port, user, password, database, ssl); var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var report = await new ContentIdOccupancyService().InspectAsync(liveDomain, profile, capabilities, dbc, schema, cancellationToken: cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"Domain\t{report.Domain}\nRegistryNamespace\t{report.RegistryNamespace}\nComplete\t{report.Complete}\nOccupiedIDs\t{report.OccupiedIds.Count}\nMaximumOccupied\t{report.MaximumOccupied?.ToString() ?? "none"}");
            foreach (var source in report.Sources) Console.WriteLine($"SOURCE\t{source.Kind}\t{source.Name}\t{(source.Available ? "AVAILABLE" : "MISSING")}\t{source.Ids}\t{source.Location}\t{source.Detail}");
            foreach (var warning in report.Warnings) Console.Error.WriteLine($"WARNING\t{warning}");
        }
        if (!reserveLive) return report.Complete ? 0 : 3;
        if (!report.Complete) { Console.Error.WriteLine("Refusing to reserve IDs because one or more authoritative occupancy sources could not be read."); return 3; }
        var result = CrucibleContentProjectService.ReserveVerifiedIds(args[1], report, liveCount, start, purpose);
        Console.WriteLine(string.Join(Environment.NewLine, result.Reservation.Values));
        Console.Error.WriteLine($"Reserved {result.Reservation.Values.Count:N0} collision-checked {liveDomain} ID(s), {result.Reservation.Values.First():N0}–{result.Reservation.Values.Last():N0}, in namespace {report.RegistryNamespace} for {result.Reservation.Purpose}."); return 0;
    }
    return ProjectHelp(2);
}

static int ProjectHelp(int code = 0) => GroupHelp($"Usage:\n  wowcrucible project create <folder> <name> [--target={TargetProfileCatalog.DefaultProfileId}] [--asset-library=folder]\n  wowcrucible project status <project-folder>\n  wowcrucible project reserve-ids <project-folder> <domain> <count> [--start=N] [--occupied=ids.txt] [--purpose=text]\n  wowcrucible project occupancy <domain> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--format=text|json]\n  wowcrucible project reserve-live <project-folder> <domain> <count> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--start=N] [--purpose=text]\n\nLive commands read passwords from WOW_CRUCIBLE_DB_PASSWORD by default and refuse reservation unless every mapped SQL/DBC identity source is available. Mount and Spell deliberately share the same registry namespace.\n\nID domains: Item, ItemSet, Spell, CreatureTemplate, CreatureModelData, CreatureDisplayInfo, CreatureDisplayInfoExtra, GameObject, Race, Class, Faction, Mount, Quest, Custom", code);

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
    if (args is ["dbc-apply", var applyServerFolder, var applyBundleRoot])
    {
        var applyWorkspace = await ServerWorkspaceDetector.DetectAsync(applyServerFolder);
        var result = await new DbcSqlDeploymentBundleService().ApplyAsync(applyBundleRoot, applyWorkspace.WorldDatabase, CancellationToken.None);
        Console.WriteLine($"RECEIPT\t{result.ReceiptPath}"); Console.WriteLine($"SERVER_SHA256\t{result.ServerSha256}");
        Console.WriteLine($"SQL_ROWS\t{result.SqlRows}"); Console.WriteLine($"RESTART\t{result.Restart}");
        Console.Error.WriteLine($"Verified synchronized deployment of {result.SqlRows:N0} SQL row(s) plus the server DBC. Receipt: {result.ReceiptPath}. Required next step: {result.Restart}.");
        return 0;
    }
    if (args is ["dbc-rollback", var rollbackServerFolder, var receiptPath])
    {
        var rollbackWorkspace = await ServerWorkspaceDetector.DetectAsync(rollbackServerFolder);
        var result = await new DbcSqlDeploymentBundleService().RollbackAsync(receiptPath, rollbackWorkspace.WorldDatabase, CancellationToken.None);
        Console.WriteLine($"RECEIPT\t{result.ReceiptPath}"); Console.WriteLine($"SQL_ROWS\t{result.SqlRows}"); Console.WriteLine($"RESTORED_SERVER_SHA256\t{result.RestoredServerSha256 ?? "<file removed>"}");
        Console.Error.WriteLine($"Verified rollback of {result.SqlRows:N0} SQL row(s) and the server DBC pre-image. Restart worldserver before runtime testing.");
        return 0;
    }
    if (args is ["dbc-module-export", var exportBundleRoot, var moduleRoot])
    {
        var path = new DbcSqlDeploymentBundleService().ExportModuleMigration(exportBundleRoot, moduleRoot); Console.WriteLine(path);
        Console.Error.WriteLine($"Exported the reviewed idempotent world migration without connecting to a database: {path}"); return 0;
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
        var source = Option(auditOptions, "--source="); var migration = Option(auditOptions, "--migration="); var bundleOutput = Option(auditOptions, "--bundle=");
        var showAll = auditOptions.Any(option => option.Equals("--all", StringComparison.OrdinalIgnoreCase)); var summaryOnly = auditOptions.Any(option => option.Equals("--summary", StringComparison.OrdinalIgnoreCase));
        var unknown = auditOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--migration=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--bundle=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--all", StringComparison.OrdinalIgnoreCase) && !option.Equals("--summary", StringComparison.OrdinalIgnoreCase)).ToArray();
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
        if (bundleOutput is not null)
        {
            var serverDbcPath = Path.Combine(auditWorkspace.DbcPath, Path.GetFileName(dbcPath));
            var bundle = new DbcSqlDeploymentBundleService().Create(bundleOutput, auditWorkspace.WorldDatabase, audit, resolution, schemaPath, serverDbcPath);
            Console.Error.WriteLine($"Created verified portable DBC/SQL deployment bundle for {bundle.Plan.Rows.Count:N0} row(s): {bundle.RootPath}");
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
    var text = "Usage:\n  wowcrucible server detect <installed-server-folder>\n  wowcrucible server inspect <installed-server-folder>\n  wowcrucible server bindings <installed-server-folder> [--source=core-source]\n  wowcrucible server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all|--summary] [--migration=output.sql] [--bundle=folder]\n  wowcrucible server dbc-apply <installed-server-folder> <bundle-folder>\n  wowcrucible server dbc-rollback <installed-server-folder> <deployment-receipt.json>\n  wowcrucible server dbc-module-export <bundle-folder> <module-root>\n  wowcrucible server client-plan <installed-server-folder> <extracted-dbc-root> [--source=core-source] [--output=plan.json] [--stage=review-folder]";
    if (code == 0) Console.WriteLine(text); else Console.Error.WriteLine(text); return code;
}

static async Task<int> Database(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Console.WriteLine("  wowcrucible db pet-compare <host> <port> <user> <database> <left-creature> <right-creature> [--levels=1-80] [--metric=hp] [--output=report] [--overwrite] [--format=text|json] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db pet-preview <host> <port> <user> <database> <creature-entry> --dbc=folder [--schema=definitions.xml] [--library=processed-assets] [--format=text|json]"); return DatabaseHelp(); }
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
    if (args[0].Equals("sync-inspect", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2) return DatabaseHelp(2); var options = args[2..]; var sqlOutput = Option(options, "--sql="); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--sql=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-inspect option: {unknown[0]}");
        var service = new DatabaseSynchronizationService(); var plan = await service.LoadPlanAsync(args[1], cancellationToken);
        Console.WriteLine($"Format\t{plan.Format}\t{plan.FormatVersion}\nCreatedUtc\t{plan.CreatedUtc:O}\nTarget\t{plan.Target.User}@{plan.Target.Host}:{plan.Target.Port}/{plan.Target.Database}\nOperations\t{plan.Operations.Count}\nIdRemaps\t{plan.IdRemaps.Count}\nReady\t{plan.Ready}\nAlreadyApplied\t{plan.AlreadyApplied}\nConflicts\t{plan.Conflicts}\nBlocked\t{plan.Blocked}\nRemovalsIncluded\t{plan.RemovalsIncluded}\nContentSha256\t{plan.ContentSha256}");
        foreach (var remap in plan.IdRemaps) Console.WriteLine($"REMAP\t{remap.Table}\t{remap.Column}\t{remap.SourceId}\t{remap.TargetId}\t{remap.RewrittenReferences}");
        foreach (var rowOperation in plan.Operations) Console.WriteLine($"ROW\t{rowOperation.Status}\t{rowOperation.Kind}\t{rowOperation.Domain}\t{rowOperation.Identity}\t{rowOperation.Finding}"); foreach (var warning in plan.Warnings) Console.Error.WriteLine($"WARNING: {warning}");
        if (sqlOutput is not null) { var output = Path.GetFullPath(sqlOutput); if (File.Exists(output) && !overwrite) return Fail($"SQL preview already exists: {output}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, service.PreviewSql(plan), cancellationToken); Console.Error.WriteLine($"Non-committing SQL preview: {output}"); }
        return plan.Conflicts == 0 && plan.Blocked == 0 ? 0 : 3;
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
    if (operation.Equals("pet-preview", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || !uint.TryParse(args[5], out var creatureEntry) || creatureEntry == 0) return Fail("db pet-preview requires a positive creature entry after the database name.");
        var options = args[6..]; var dbc = Option(options, "--dbc="); var schema = Option(options, "--schema="); var library = Option(options, "--library="); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--schema=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--library=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown pet-preview option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(dbc) || !Directory.Exists(dbc)) return Fail("--dbc must point to the server DBC folder containing CreatureDisplayInfo.dbc and CreatureModelData.dbc.");
        if (schema is not null && !File.Exists(schema)) return Fail($"Creature preview schema does not exist: {Path.GetFullPath(schema)}");
        if (library is not null && !Directory.Exists(library)) return Fail($"Processed asset library does not exist: {Path.GetFullPath(library)}");
        var resolved = (await new CreatureDisplayPreviewService().ResolveCreaturesAsync(profile, dbc, schema, library, [creatureEntry], cancellationToken)).Single();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(resolved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"CREATURE\t{resolved.CreatureEntry}\t{resolved.Name}\t{resolved.Finding}");
            foreach (var display in resolved.Displays)
            {
                Console.WriteLine($"DISPLAY\t{display.DisplayId}\tmodel={display.ModelId}\tpath={display.ModelClientPath}\tdisplay-scale={display.DisplayScale:0.###}\tmodel-scale={display.ModelScale:0.###}\t{display.Finding}");
                foreach (var source in display.Sources) Console.WriteLine($"SOURCE\t{(source.Ready ? "READY" : "MISSING_SKIN")}\t{source.Provenance}\t{source.ModelPath}\t{source.SkinPath}\ttextures={source.CreatureTextures.Count}");
            }
        }
        var ready = resolved.Displays.Sum(display => display.Sources.Count(source => source.Ready)); Console.Error.WriteLine($"Resolved {resolved.Displays.Count:N0} display(s) and {ready:N0} ready same-provenance M2/SKIN source(s) for creature {creatureEntry:N0}.");
        return library is not null && ready == 0 ? 3 : resolved.Displays.Count == 0 ? 3 : 0;
    }
    if (operation.Equals("pet-curve", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db pet-curve requires <source-creature> <target-creature> after the database name.");
        if (!uint.TryParse(args[5], out var sourceCreature) || sourceCreature == 0 || !uint.TryParse(args[6], out var targetCreature) || targetCreature == 0) return Fail("Pet curve source and target creature entries must be positive unsigned integers.");
        var options = args[7..]; var levels = Option(options, "--levels=") ?? "1-80"; var parts = levels.Split('-', 2, StringSplitOptions.TrimEntries); if (parts.Length != 2 || !byte.TryParse(parts[0], out var startLevel) || !byte.TryParse(parts[1], out var endLevel) || startLevel == 0 || endLevel < startLevel) return Fail("--levels must be an inclusive range such as 1-80 within 1-255.");
        static decimal ScaleOption(string? text, string name) { if (text is null) return 1m; if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)) throw new ArgumentException($"--{name} must be an invariant decimal number."); return value; }
        var health = ScaleOption(Option(options, "--health="), "health"); var mana = ScaleOption(Option(options, "--mana="), "mana"); var armor = ScaleOption(Option(options, "--armor="), "armor"); var attributes = ScaleOption(Option(options, "--attributes="), "attributes"); var damage = ScaleOption(Option(options, "--damage="), "damage");
        var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var update = options.Any(option => option.Equals("--update-existing", StringComparison.OrdinalIgnoreCase)); var output = Option(options, "--output="); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--levels=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--health=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--mana=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--armor=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--attributes=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--damage=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--update-existing", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown pet-curve option: {unknown[0]}"); if (update && !apply) return Fail("--update-existing changes exact target rows and therefore also requires --apply.");
        var service = new PetLevelCurveService(); var request = new PetLevelCurveRequest(sourceCreature, targetCreature, startLevel, endLevel, new(health, mana, armor, attributes, damage)); var prepared = await service.PrepareAsync(profile, request, cancellationToken); var mode = update ? PetLevelCurveWriteMode.UpdateExactRange : PetLevelCurveWriteMode.InsertMissing; var sql = service.PreviewSql(prepared, mode) + Environment.NewLine; var existing = prepared.ExpectedTargetRows.Count(pair => pair.Value is not null); var missing = prepared.ExpectedTargetRows.Count - existing;
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Request = request, Mode = mode, Rows = prepared.Content.Rows.Count, ExistingTargetRows = existing, MissingTargetRows = missing, prepared.Content.OmittedFields, Sql = sql }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })); else Console.Write(sql);
        if (output is not null) { var path = Path.GetFullPath(output); if (File.Exists(path) && !overwrite) return Fail($"Pet curve SQL already exists: {path}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, sql, cancellationToken); Console.Error.WriteLine($"Pet curve SQL: {path}"); }
        Console.Error.WriteLine($"Prepared {prepared.Content.Rows.Count:N0} source-backed level row(s) for creature {targetCreature}: {existing:N0} existing, {missing:N0} missing. Policy: {mode}.");
        if (!apply) { Console.Error.WriteLine("Dry-run only. Re-run with --apply to insert missing rows; add --update-existing only when the reviewed range should replace exact existing levels."); return 0; }
        var result = await service.ApplyAsync(profile, prepared, mode, cancellationToken); Console.Error.WriteLine($"Committed pet curve transactionally: {result.Inserted:N0} inserted, {result.Updated:N0} updated, {result.Skipped:N0} preserved."); return 0;
    }
    if (operation.Equals("pet-compare", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db pet-compare requires <left-creature> <right-creature> after the database name.");
        if (!uint.TryParse(args[5], out var leftCreature) || leftCreature == 0 || !uint.TryParse(args[6], out var rightCreature) || rightCreature == 0) return Fail("Pet comparison creature entries must be positive unsigned integers.");
        var options = args[7..]; var levels = Option(options, "--levels=") ?? "1-80"; var parts = levels.Split('-', 2, StringSplitOptions.TrimEntries); if (parts.Length != 2 || !byte.TryParse(parts[0], out var startLevel) || !byte.TryParse(parts[1], out var endLevel) || startLevel == 0 || endLevel < startLevel) return Fail("--levels must be an inclusive range such as 1-80 within 1-255.");
        var metricName = Option(options, "--metric="); var output = Option(options, "--output="); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--levels=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--metric=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown pet-compare option: {unknown[0]}");
        var comparison = await new PetLevelCurveService().CompareAsync(profile, new(leftCreature, rightCreature, startLevel, endLevel), cancellationToken); var selected = metricName is null ? null : comparison.Metrics.FirstOrDefault(metric => metric.Column.Equals(metricName, StringComparison.OrdinalIgnoreCase)); if (metricName is not null && selected is null) return Fail($"Unknown pet comparison metric '{metricName}'. Available: {string.Join(", ", comparison.Metrics.Select(metric => metric.Column))}");
        static string PetPercent(decimal? value) => value is null ? "n/a" : value.Value.ToString("+0.###;-0.###;0", System.Globalization.CultureInfo.InvariantCulture) + "%"; static string PetNumber(decimal? value) => value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "missing";
        string rendered;
        if (json) { object payload = selected is null ? comparison : new { comparison.Request, Metric = selected, comparison.MissingLeftLevels, comparison.MissingRightLevels }; rendered = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
        else
        {
            var lines = new List<string> { $"COMPARE\t{leftCreature}\t{rightCreature}\t{startLevel}-{endLevel}\tleft-missing={comparison.MissingLeftLevels.Count}\tright-missing={comparison.MissingRightLevels.Count}" };
            foreach (var metric in selected is null ? comparison.Metrics : [selected]) { lines.Add($"METRIC\t{metric.Column}\t{metric.Display}\tleft-growth={PetPercent(metric.LeftGrowthPercent)}\tright-growth={PetPercent(metric.RightGrowthPercent)}\tend-delta={PetPercent(metric.EndDeltaPercent)}\taverage-delta={PetPercent(metric.AverageDeltaPercent)}\tpaired={metric.PairedLevels}"); if (selected is not null) { lines.Add("LEVEL\tLEFT\tRIGHT\tRIGHT_VS_LEFT"); lines.AddRange(metric.Points.Select(point => $"{point.Level}\t{PetNumber(point.Left)}\t{PetNumber(point.Right)}\t{PetPercent(point.DeltaPercent)}")); } }
            if (comparison.MissingLeftLevels.Count > 0) lines.Add($"MISSING_LEFT\t{string.Join(',', comparison.MissingLeftLevels)}"); if (comparison.MissingRightLevels.Count > 0) lines.Add($"MISSING_RIGHT\t{string.Join(',', comparison.MissingRightLevels)}"); rendered = string.Join(Environment.NewLine, lines);
        }
        Console.WriteLine(rendered); if (output is not null) { var path = Path.GetFullPath(output); if (File.Exists(path) && !overwrite) return Fail($"Pet comparison output already exists: {path}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, rendered + Environment.NewLine, cancellationToken); Console.Error.WriteLine($"Pet comparison: {path}"); }
        Console.Error.WriteLine($"Compared {comparison.Metrics.Count:N0} numeric stat column(s), {comparison.MissingLeftLevels.Count:N0} left gap(s), and {comparison.MissingRightLevels.Count:N0} right gap(s)."); return comparison.MissingLeftLevels.Count == 0 && comparison.MissingRightLevels.Count == 0 ? 0 : 3;
    }
    if (operation.Equals("favorites", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var search = Option(options, "--search="); var verify = options.Any(option => option.Equals("--verify", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--search=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--verify", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown favorites option: {unknown[0]}");
        var favorites = SqlFavoriteStore.Load().Where(favorite => SqlFavoriteWorkspaceService.Matches(favorite, search)).ToArray();
        IReadOnlyList<SqlFavoriteVerification> checks = verify
            ? await new SqlFavoriteWorkspaceService().VerifyAsync(profile, favorites, cancellationToken)
            : favorites.Select(favorite => new SqlFavoriteVerification(favorite.Identity, SqlFavoriteVerificationState.Unchecked, "Not checked; add --verify for exact live primary-key validation.", DateTimeOffset.MinValue)).ToArray();
        var byIdentity = checks.ToDictionary(check => check.Identity, StringComparer.OrdinalIgnoreCase);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(favorites.Select(favorite => new { Favorite = favorite, Verification = byIdentity[favorite.Identity] }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else foreach (var favorite in favorites) { var check = byIdentity[favorite.Identity]; Console.WriteLine($"{check.Display}\t{favorite.Database}\t{favorite.Table}\t{string.Join(",", favorite.Key.Select(pair => $"{pair.Key}={pair.Value}"))}\t{favorite.Label}\t{favorite.Notes}\t{check.Detail}"); }
        Console.Error.WriteLine($"{favorites.Length:N0} favorite(s){(verify ? $" · {checks.Count(check => check.State == SqlFavoriteVerificationState.Live):N0} live · {checks.Count(check => check.State == SqlFavoriteVerificationState.Missing):N0} missing · {checks.Count(check => check.State is SqlFavoriteVerificationState.SchemaMismatch or SqlFavoriteVerificationState.Error):N0} changed/failed" : string.Empty)}. Store: {CruciblePaths.SqlFavoritesFile}");
        return verify && checks.Any(check => check.State != SqlFavoriteVerificationState.Live) ? 3 : 0;
    }
    if (operation.Equals("sync-plan", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db sync-plan requires <verified-audit> <output-plan.json>.");
        var options = args[7..]; var includes = options.Where(option => option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray(); var removals = options.Any(option => option.Equals("--include-removals", StringComparison.OrdinalIgnoreCase)); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var autoRemap = options.Any(option => option.Equals("--auto-remap", StringComparison.OrdinalIgnoreCase)); var maximumText = Option(options, "--maximum=") ?? "100000"; var remapStartText = Option(options, "--remap-start=");
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--maximum=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--remap-start=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--include-removals", StringComparison.OrdinalIgnoreCase) && !option.Equals("--auto-remap", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-plan option: {unknown[0]}"); if (!int.TryParse(maximumText, out var maximum) || maximum is < 1 or > 1_000_000) return Fail("--maximum must be from 1 through 1,000,000."); if (remapStartText is not null && (!uint.TryParse(remapStartText, out _) || remapStartText == "0")) return Fail("--remap-start must be a positive unsigned integer."); uint? remapStart = remapStartText is null ? null : uint.Parse(remapStartText);
        var progress = new Progress<(string Stage, string? Table, int Completed, int Total)>(value => Console.Error.WriteLine(value.Table is null ? value.Stage : $"{value.Stage}\t{value.Completed:N0}/{value.Total:N0}\t{value.Table}"));
        var result = await new DatabaseSynchronizationService().BuildPlanAsync(args[5], profile, args[6], new(includes, removals, maximum, overwrite, autoRemap, remapStart), progress, cancellationToken); var plan = result.Plan;
        foreach (var remap in plan.IdRemaps) Console.WriteLine($"REMAP\t{remap.Table}\t{remap.Column}\t{remap.SourceId}\t{remap.TargetId}\t{remap.RewrittenReferences}");
        foreach (var rowOperation in plan.Operations) Console.WriteLine($"ROW\t{rowOperation.Status}\t{rowOperation.Kind}\t{rowOperation.Domain}\t{rowOperation.Identity}\t{rowOperation.Finding}");
        Console.Error.WriteLine($"Target comparison plan: {plan.Ready:N0} ready, {plan.AlreadyApplied:N0} already applied, {plan.Conflicts:N0} conflict(s), {plan.Blocked:N0} blocked. Artifact: {result.Path}"); return plan.Conflicts == 0 && plan.Blocked == 0 ? 0 : 3;
    }
    if (operation.Equals("sync-apply", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db sync-apply requires <plan.json> <receipt.json>."); var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-apply option: {unknown[0]}"); var service = new DatabaseSynchronizationService(); var plan = await service.LoadPlanAsync(args[5], cancellationToken);
        Console.WriteLine($"Target\t{plan.Target.User}@{plan.Target.Host}:{plan.Target.Port}/{plan.Target.Database}\nReady\t{plan.Ready}\nAlreadyApplied\t{plan.AlreadyApplied}\nConflicts\t{plan.Conflicts}\nBlocked\t{plan.Blocked}"); if (!apply) { Console.Error.WriteLine("Dry-run only. Re-run with --apply only after the target binding, conflicts, and receipt path are reviewed."); return plan.Conflicts == 0 && plan.Blocked == 0 ? 0 : 3; }
        var result = await service.ApplyAsync(args[5], profile, args[6], overwrite, cancellationToken); Console.Error.WriteLine($"Committed {result.Applied:N0} exact row operation(s); {result.AlreadyApplied:N0} were already applied. Rollback receipt: {result.ReceiptPath}"); return 0;
    }
    if (operation.Equals("sync-rollback", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db sync-rollback requires <receipt.json>."); var options = args[6..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-rollback option: {unknown[0]}");
        if (!apply) { Console.Error.WriteLine("Dry-run only. This receipt is unchanged; re-run with --apply to revalidate every postimage and roll back transactionally."); return 0; } var result = await new DatabaseSynchronizationService().RollbackAsync(args[5], profile, cancellationToken); Console.Error.WriteLine($"Rolled back {result.Applied:N0} exact row operation(s); {result.AlreadyApplied:N0} were already at their pre-apply state. Receipt marked rolled back: {result.ReceiptPath}"); return 0;
    }
    if (operation.Equals("schemas", StringComparison.OrdinalIgnoreCase))
    {
        var unknown = args[5..].Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown schemas option: {unknown[0]}");
        var schemas = await new SqlWorkspaceService().ListDatabasesAsync(profile, cancellationToken); foreach (var schema in schemas) Console.WriteLine(schema); Console.Error.WriteLine($"{schemas.Count:N0} accessible database schema(s)."); return 0;
    }
    if (operation.Equals("rows", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db rows requires a table name.");
        var options = args[6..]; var search = Option(options, "--search="); var filterText = Option(options, "--filter="); var sort = Option(options, "--sort=");
        var limitText = Option(options, "--limit=") ?? "200"; var offsetText = Option(options, "--offset=") ?? "0"; var descending = options.Any(option => option.Equals("--descending", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--search=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--sort=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--offset=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--descending", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown rows option: {unknown[0]}"); if (!int.TryParse(limitText, out var limit) || limit is < 1 or > 500) return Fail("--limit must be from 1 to 500."); if (!int.TryParse(offsetText, out var offset) || offset < 0) return Fail("--offset must be zero or greater.");
        string? filterColumn = null; string? filterValue = null;
        if (filterText is not null) { var split = filterText.IndexOf('='); if (split <= 0) return Fail("--filter must use column=value."); filterColumn = filterText[..split]; filterValue = filterText[(split + 1)..]; }
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var page = await new SqlWorkspaceService().ReadPageAsync(profile, table, offset, limit, search, filterColumn, filterValue, sort, descending, cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(page, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else { Console.WriteLine(string.Join('\t', page.Columns.Select(column => column.Name))); foreach (var row in page.Rows) Console.WriteLine(string.Join('\t', page.Columns.Select(column => row.Values.TryGetValue(column.Name, out var value) ? SqlCell(value) : string.Empty))); }
        Console.Error.WriteLine($"Returned {page.Rows.Count:N0} of {page.TotalRows:N0} matching {page.Table} row(s) at offset {page.Offset:N0}; {page.Columns.Count:N0} complete column(s)."); return 0;
    }
    if (operation.Equals("table-admin", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db table-admin requires a table name."); var options = args[6..]; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown table-admin option: {unknown[0]}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}"); var administration = new SqlAdministrationService(); var ddl = await administration.ShowCreateTableAsync(profile, table, cancellationToken); var indexes = await administration.ReadIndexesAsync(profile, table, cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Database = profile.Database, Table = table.Name, Columns = table.Columns, Indexes = indexes, CreateTable = ddl }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { Console.WriteLine($"DATABASE\t{profile.Database}\nTABLE\t{table.Name}\nCOLUMNS\t{table.Columns.Count}\nINDEXES\t{indexes.Count}"); foreach (var index in indexes) Console.WriteLine($"INDEX\t{index.Display}"); Console.WriteLine($"DDL\n{ddl}"); } return 0;
    }
    if (operation.Equals("objects", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var typeText = Option(options, "--type="); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--type=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown objects option: {unknown[0]}");
        SqlDatabaseObjectType? type = typeText is null ? null : ParseDatabaseObjectType(typeText); var objects = await new SqlDatabaseObjectService().ListAsync(profile, cancellationToken); if (type is not null) objects = objects.Where(item => item.Type == type).ToArray();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(objects, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var item in objects) Console.WriteLine($"{item.Type}\t{item.Name}\t{item.Definer}\t{item.State ?? "-"}\t{item.Details}"); Console.Error.WriteLine($"{objects.Count:N0} visible view/routine/trigger/event object(s) in {profile.Database}."); return 0;
    }
    if (operation.Equals("object-show", StringComparison.OrdinalIgnoreCase) || operation.Equals("object-drop", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail($"db {operation} requires <view|trigger|procedure|function|event> <name>.");
        var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown {operation} option: {unknown[0]}");
        if (operation.Equals("object-show", StringComparison.OrdinalIgnoreCase) && apply) return Fail("object-show is read-only and does not accept --apply.");
        var type = ParseDatabaseObjectType(args[5]); var service = new SqlDatabaseObjectService(); var objects = await service.ListAsync(profile, cancellationToken); var item = objects.FirstOrDefault(candidate => candidate.Type == type && candidate.Name.Equals(args[6], StringComparison.OrdinalIgnoreCase)); if (item is null) return Fail($"{type} not found: {args[6]}");
        if (operation.Equals("object-show", StringComparison.OrdinalIgnoreCase)) { var definition = await service.ShowCreateAsync(profile, item, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(definition, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else Console.WriteLine(definition.CreateSql); return 0; }
        var sql = SqlDatabaseObjectService.BuildDropSql(item); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run DROP plan only. Re-run with --apply after exporting or reviewing the exact definition."); return 0; } await service.DropAsync(profile, item, cancellationToken); Console.Error.WriteLine($"Dropped exact {item.Type} {profile.Database}.{item.Name}."); return 0;
    }
    if (operation.Equals("object-export", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db object-export requires an output .sql path."); var options = args[6..]; var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown object-export option: {unknown[0]}");
        var result = await new SqlDatabaseObjectService().ExportAsync(profile, args[5], overwrite, cancellationToken); Console.WriteLine(result.Path); Console.Error.WriteLine($"Atomically exported {result.Objects:N0} exact database-object definition(s), {result.Bytes:N0} bytes."); return 0;
    }
    if (operation.Equals("view-set", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db view-set requires <view-name> <select.sql>."); var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown view-set option: {unknown[0]}"); if (!File.Exists(args[6])) return Fail($"SELECT file not found: {args[6]}");
        var select = await File.ReadAllTextAsync(args[6], cancellationToken); var sql = SqlDatabaseObjectService.BuildCreateOrReplaceViewSql(profile.Database, args[5], select); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run CREATE OR REPLACE VIEW plan only. Re-run with --apply after reviewing the exact SELECT."); return 0; } await new SqlDatabaseObjectService().CreateOrReplaceViewAsync(profile, args[5], select, cancellationToken); Console.Error.WriteLine($"Created or replaced view {profile.Database}.{args[5]}."); return 0;
    }
    if (operation.Equals("event-state", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db event-state requires <event-name> <enable|disable>."); var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown event-state option: {unknown[0]}"); var enabled = args[6].Equals("enable", StringComparison.OrdinalIgnoreCase); if (!enabled && !args[6].Equals("disable", StringComparison.OrdinalIgnoreCase)) return Fail("Event state must be enable or disable.");
        var service = new SqlDatabaseObjectService(); var item = (await service.ListAsync(profile, cancellationToken)).FirstOrDefault(candidate => candidate.Type == SqlDatabaseObjectType.Event && candidate.Name.Equals(args[5], StringComparison.OrdinalIgnoreCase)); if (item is null) return Fail($"Event not found: {args[5]}"); var sql = SqlDatabaseObjectService.BuildEventStateSql(item, enabled); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run ALTER EVENT plan only. Re-run with --apply after review."); return 0; } await service.SetEventEnabledAsync(profile, item, enabled, cancellationToken); Console.Error.WriteLine($"Event {profile.Database}.{item.Name} is now {(enabled ? "enabled" : "disabled")}."); return 0;
    }
    if (operation.Equals("process-list", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown process-list option: {unknown[0]}");
        var processes = await new SqlAdministrationService().ReadProcessesAsync(profile, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(processes, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var process in processes) Console.WriteLine($"{process.Id}\t{process.User}\t{process.Host}\t{process.Database}\t{process.Command}\t{process.Seconds}\t{process.State}\t{process.Statement}"); Console.Error.WriteLine($"{processes.Count:N0} visible process(es)."); return 0;
    }
    if (operation.Equals("user-list", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown user-list option: {unknown[0]}");
        var users = await new SqlAdministrationService().ReadUsersAsync(profile, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(users, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var account in users) Console.WriteLine(account.Display); Console.Error.WriteLine($"{users.Count:N0} visible database account(s); password hashes were not queried."); return 0;
    }
    if (operation.Equals("account", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 8) return Fail("db account requires <grants|create|password|lock|unlock|grant|revoke|drop> <account-user> <account-host>.");
        var action = args[5].ToLowerInvariant(); var accountUser = args[6]; var accountHost = args[7]; var privilegeAction = action is "grant" or "revoke"; if (privilegeAction && args.Length < 9) return Fail($"db account {action} requires a comma-separated privilege list.");
        var optionStart = privilegeAction ? 9 : 8; var options = args[optionStart..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var locked = options.Any(option => option.Equals("--locked", StringComparison.OrdinalIgnoreCase)); var global = options.Any(option => option.Equals("--global", StringComparison.OrdinalIgnoreCase)); var grantOption = options.Any(option => option.Equals("--grant-option", StringComparison.OrdinalIgnoreCase)); var table = Option(options, "--table="); var newPasswordEnvironment = Option(options, "--new-password-env=") ?? "WOW_CRUCIBLE_NEW_DB_PASSWORD";
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--new-password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--table=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--locked", StringComparison.OrdinalIgnoreCase) && !option.Equals("--global", StringComparison.OrdinalIgnoreCase) && !option.Equals("--grant-option", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown account option: {unknown[0]}"); if (string.IsNullOrWhiteSpace(newPasswordEnvironment)) return Fail("--new-password-env requires a non-empty environment-variable name.");
        if (action is not ("grants" or "create" or "password" or "lock" or "unlock" or "grant" or "revoke" or "drop")) return Fail($"Unknown account action: {action}");
        if (action != "create" && locked) return Fail("--locked applies only to account creation."); if (!privilegeAction && (global || grantOption || table is not null)) return Fail("--global, --table, and --grant-option apply only to grant/revoke."); if (action == "revoke" && grantOption) return Fail("--grant-option applies only to grant.");
        var administration = new SqlAdministrationService();
        if (action == "grants")
        {
            if (apply) return Fail("account grants is read-only and does not accept --apply."); var grants = await administration.ReadGrantsAsync(profile, accountUser, accountHost, cancellationToken);
            if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(grants, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var grant in grants) Console.WriteLine(grant); Console.Error.WriteLine($"{grants.Count:N0} grant statement(s) visible for '{accountUser}'@'{accountHost}'."); return 0;
        }
        IReadOnlyList<SqlPrivilegeInfo> supportedPrivileges = []; IReadOnlyList<string> privileges = [];
        if (privilegeAction) { supportedPrivileges = await administration.ReadPrivilegesAsync(profile, cancellationToken); privileges = args[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
        var sql = action switch
        {
            "create" => SqlAdministrationService.BuildCreateUserSql(accountUser, accountHost, locked),
            "password" => SqlAdministrationService.BuildChangePasswordSql(accountUser, accountHost),
            "lock" => SqlAdministrationService.BuildAccountLockSql(accountUser, accountHost, true),
            "unlock" => SqlAdministrationService.BuildAccountLockSql(accountUser, accountHost, false),
            "drop" => SqlAdministrationService.BuildDropUserSql(accountUser, accountHost),
            "grant" => SqlAdministrationService.BuildGrantSql(accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges, grantOption),
            "revoke" => SqlAdministrationService.BuildRevokeSql(accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges),
            _ => throw new UnreachableException()
        };
        Console.WriteLine(SqlAdministrationService.RedactPasswordSql(sql)); if (!apply) { Console.Error.WriteLine("Dry-run account plan only. Re-run with --apply after reviewing the exact account, host, scope, and privileges."); return 0; }
        if (action is "create" or "password")
        {
            var newPassword = Environment.GetEnvironmentVariable(newPasswordEnvironment); if (string.IsNullOrEmpty(newPassword)) return Fail($"Set {newPasswordEnvironment} for --apply. New passwords are never accepted as command arguments or printed.");
            if (action == "create") await administration.CreateUserAsync(profile, accountUser, accountHost, newPassword, locked, cancellationToken); else await administration.ChangePasswordAsync(profile, accountUser, accountHost, newPassword, cancellationToken);
        }
        else if (action == "lock") await administration.SetAccountLockAsync(profile, accountUser, accountHost, true, cancellationToken);
        else if (action == "unlock") await administration.SetAccountLockAsync(profile, accountUser, accountHost, false, cancellationToken);
        else if (action == "drop") await administration.DropUserAsync(profile, accountUser, accountHost, cancellationToken);
        else if (action == "grant") await administration.GrantAsync(profile, accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges, grantOption, cancellationToken);
        else await administration.RevokeAsync(profile, accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges, cancellationToken);
        Console.Error.WriteLine($"Applied account {action} for '{accountUser}'@'{accountHost}'. Verify the result with db account ... grants."); return 0;
    }
    if (operation.Equals("join", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db join requires a recognized relationship name. Use db inspect to review relationships."); var options = args[6..]; var joinType = Option(options, "--type=") ?? "LEFT"; var limitText = Option(options, "--limit=") ?? "200"; var run = options.Any(option => option.Equals("--run", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--type=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--run", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown join option: {unknown[0]}"); if (!int.TryParse(limitText, out var joinLimit)) return Fail("--limit must be numeric.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var relation = capabilities.Relationships.FirstOrDefault(candidate => candidate.Name.Equals(args[5], StringComparison.OrdinalIgnoreCase)); if (relation is null) return Fail($"Relationship not found: {args[5]}"); var source = capabilities.FindTable(relation.FromTable) ?? throw new InvalidOperationException("Relationship source table is unavailable."); var target = capabilities.FindTable(relation.ToTable) ?? throw new InvalidOperationException("Relationship target table is unavailable."); var sql = SqlAdministrationService.BuildJoinSql(relation, source, target, joinType, joinLimit); if (!run) { Console.WriteLine(sql); Console.Error.WriteLine("Dry-run join SQL only. Re-run with --run to execute the read-only SELECT."); return 0; }
        var result = await new SqlWorkspaceService().QueryAsync(profile, sql, 2000, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { Console.WriteLine(string.Join('\t', result.Columns)); foreach (var row in result.Rows) Console.WriteLine(string.Join('\t', row.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)))); } Console.Error.WriteLine($"Join returned {result.Rows.Count:N0} row(s) in {result.Duration.TotalMilliseconds:N0} ms."); return 0;
    }
    if (operation.Equals("index", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 8) return Fail("db index requires <table> <create|drop> <index-name>; create also requires a comma-separated column list."); var tableName = args[5]; var action = args[6]; var indexName = args[7]; var create = action.Equals("create", StringComparison.OrdinalIgnoreCase); var drop = action.Equals("drop", StringComparison.OrdinalIgnoreCase); if (!create && !drop) return Fail("Index action must be create or drop."); if (create && args.Length < 9) return Fail("Index create requires a comma-separated column list.");
        var optionStart = create ? 9 : 8; var options = args[optionStart..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var unique = options.Any(option => option.Equals("--unique", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--unique", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown index option: {unknown[0]}"); if (drop && unique) return Fail("--unique applies only to index create.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(tableName); if (table is null) return Fail($"Table not found: {tableName}"); var columns = create ? args[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : []; var sql = create ? SqlAdministrationService.BuildCreateIndexSql(table, indexName, columns, unique) : SqlAdministrationService.BuildDropIndexSql(table, indexName); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run DDL only. Re-run with --apply after review; MySQL may implicitly commit schema changes."); return 0; }
        var administration = new SqlAdministrationService(); if (create) await administration.CreateIndexAsync(profile, table, indexName, columns, unique, cancellationToken); else await administration.DropIndexAsync(profile, table, indexName, cancellationToken); Console.Error.WriteLine($"Applied index {action} on {profile.Database}.{table.Name}."); return 0;
    }
    if (operation.Equals("dependency-snapshot", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 8 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db dependency-snapshot requires a table name, output JSON path, and one --key=column=value option per primary-key column.");
        var snapshotOptions = args[7..]; var keyOptions = snapshotOptions.Where(option => option.StartsWith("--key=", StringComparison.OrdinalIgnoreCase)).Select(option => option[6..]).ToArray();
        var limitText = Option(snapshotOptions, "--limit=") ?? "200"; var overwrite = snapshotOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = snapshotOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--key=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown dependency-snapshot option: {unknown[0]}");
        if (!int.TryParse(limitText, out var dependencyLimit) || dependencyLimit is < 1 or > 500) return Fail("--limit must be from 1 to 500 rows per edge.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray(); if (primary.Length == 0) return Fail($"{table.Name} has no primary key and cannot be snapshotted safely.");
        var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in keyOptions) { var split = option.IndexOf('='); if (split <= 0) return Fail($"Invalid --key value: {option}. Use --key=column=value."); key[option[..split]] = option[(split + 1)..]; }
        if (key.Count != primary.Length || primary.Any(column => !key.ContainsKey(column)) || key.Keys.Any(column => !primary.Contains(column, StringComparer.OrdinalIgnoreCase))) return Fail($"Supply the complete primary key exactly once: {string.Join(", ", primary.Select(column => $"--key={column}=VALUE"))}");
        var service = new SqlWorkspaceService(); var row = await service.ReadRowAsync(profile, table, key, cancellationToken); if (row is null) return Fail($"No exact {table.Name} row matches the supplied primary key.");
        var snapshot = await service.CaptureDependencySnapshotAsync(profile, capabilities, table.Name, row, dependencyLimit, cancellationToken); var output = Path.GetFullPath(args[6]); if (File.Exists(output) && !overwrite) return Fail($"Output already exists: {output}. Use --overwrite after reviewing it.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken);
        Console.Error.WriteLine($"Captured {table.Name} {row.Display}: {snapshot.Edges.Count:N0} edge(s), {snapshot.Edges.Sum(edge => edge.Rows.Count):N0} related row(s), {snapshot.Edges.Count(edge => edge.Truncated):N0} truncated edge(s), {snapshot.Edges.Count(edge => edge.TotalRows < 0):N0} file-DBC edge(s) with empty SQL mirrors. Snapshot: {output}"); return 0;
    }
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
        var queryOptions = args[6..]; var write = queryOptions.Any(option => option.Equals("--write", StringComparison.OrdinalIgnoreCase)); var batch = queryOptions.Any(option => option.Equals("--batch", StringComparison.OrdinalIgnoreCase)); var batchFormat = Option(queryOptions, "--batch-format=") ?? "text"; var queryOutput = Option(queryOptions, "--output="); var queryFormat = Option(queryOptions, "--format="); var queryOverwrite = queryOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = queryOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--batch-format=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--write", StringComparison.OrdinalIgnoreCase) && !option.Equals("--batch", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown query option: {unknown[0]}");
        if (queryOutput is null && (queryFormat is not null || queryOverwrite)) return Fail("--format and --overwrite require --output for a read-only query result.");
        var sql = await File.ReadAllTextAsync(args[5], cancellationToken);
        if (write)
        {
            if (batch) return Fail("--write and --batch are mutually exclusive. Review mutations through the single confirmed write path.");
            if (queryOutput is not null || queryFormat is not null || queryOverwrite) return Fail("Query-result output options apply only to read-only queries, not --write.");
            var result = await new SqlWorkspaceService().ExecuteAsync(profile, sql, cancellationToken); Console.WriteLine($"AffectedRows\t{result.AffectedRows}\nDurationMs\t{result.Duration.TotalMilliseconds:0}"); return 0;
        }
        if (batch)
        {
            if (queryOutput is not null || queryFormat is not null || queryOverwrite) return Fail("Batch results may have different shapes. Export a selected result in desktop SQL Studio, or omit --batch for single-result --output.");
            var result = await new SqlWorkspaceService().QueryBatchAsync(profile, sql, 10000, cancellationToken);
            if (batchFormat.Equals("json", StringComparison.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            else if (batchFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
                foreach (var set in result.Results) { Console.WriteLine($"RESULT\t{set.Index}\t{set.Result.Rows.Count}\t{set.Result.Columns.Count}\t{(set.Truncated ? "TRUNCATED" : "COMPLETE")}"); Console.WriteLine(string.Join('\t', set.Result.Columns)); foreach (var row in set.Result.Rows) Console.WriteLine(string.Join('\t', row.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)))); }
            else return Fail("--batch-format must be text or json.");
            Console.Error.WriteLine($"Returned {result.TotalRows:N0} row(s) across {result.Results.Count:N0} independently validated read result(s) in {result.Duration.TotalMilliseconds:N0} ms."); return 0;
        }
        var query = await new SqlWorkspaceService().QueryAsync(profile, sql, 10000, cancellationToken); Console.WriteLine(string.Join('\t', query.Columns)); foreach (var row in query.Rows) Console.WriteLine(string.Join('\t', row.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))));
        if (queryOutput is not null)
        {
            var format = (queryFormat ?? Path.GetExtension(queryOutput).TrimStart('.')).ToLowerInvariant() switch { "csv" => SqlExportFormat.Csv, "jsonl" or "ndjson" => SqlExportFormat.JsonLines, var value => throw new ArgumentException($"Unsupported query-result format '{value}'. Use csv or jsonl.") };
            var exported = await new SqlTransferService().ExportQueryResultAsync(query, queryOutput, format, queryOverwrite, cancellationToken); Console.Error.WriteLine($"Exported structured query result: {exported.Rows:N0} row(s) × {exported.Columns:N0} column(s) → {exported.Path}");
        }
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
        foreach (var relation in capabilities.Relationships.OrderBy(value => value.FromTable).ThenBy(value => value.Name)) Console.WriteLine($"RELATION\t{relation.Name}\t{(relation.Declared ? "declared" : "inferred")}\t{relation.FromTable}.{relation.FromColumn}\t{relation.ToTable}.{relation.ToColumn}\t{relation.Description}");
        return capabilities.Tables.Count > 0 ? 0 : 1;
    }
    if (operation.Equals("item-audit", StringComparison.OrdinalIgnoreCase))
    {
        var output = Option(rawOptions, "--output="); var dbc = Option(rawOptions, "--dbc="); var unknown = rawOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-audit option: {unknown[0]}");
        var audit = await new ItemCatalogService().AuditAsync(profile, dbc);
        foreach (var item in audit.NoKnownAcquisitionPath) Console.WriteLine($"{item.Entry}\t{item.Quality}\t{item.ItemLevel}\t{item.ItemSetId}\t{item.ReviewGroup}\t{item.Name}");
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
        Console.WriteLine($"REVIEW_GROUP\t{inspection.Item.ReviewGroup}");
        foreach (var evidence in inspection.AcceptedEvidence) Console.WriteLine($"ACCEPTED\t{evidence}");
        foreach (var evidence in inspection.RejectedEvidence) Console.WriteLine($"REJECTED\t{evidence}");
        Console.WriteLine($"COVERAGE\t{inspection.CheckedSources.Count} checked\t{inspection.MissingSources.Count} missing");
        foreach (var missing in inspection.MissingSources) Console.WriteLine($"MISSING\t{missing}");
        return 0;
    }
    if (operation.Equals("spell-inspect", StringComparison.OrdinalIgnoreCase) && args.Length >= 6 && uint.TryParse(args[5], out var inspectedSpell) && inspectedSpell > 0)
    {
        var inspectOptions = args[6..]; var dbcOption = Option(inspectOptions, "--dbc="); var json = inspectOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = inspectOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown spell-inspect option: {unknown[0]}");
        WdbcFile? spellDbc = null; IReadOnlyList<DbcColumn>? spellColumns = null;
        if (!string.IsNullOrWhiteSpace(dbcOption))
        {
            var dbcPath = Directory.Exists(dbcOption) ? Path.Combine(dbcOption, "Spell.dbc") : dbcOption;
            if (!File.Exists(dbcPath)) return Fail($"Spell.dbc was not found: {Path.GetFullPath(dbcPath)}");
            spellDbc = WdbcFile.Load(dbcPath); var resolution = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Spell", spellDbc.FieldCount);
            if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch) return Fail($"{dbcPath} has {spellDbc.FieldCount} fields; the WotLK build-12340 Spell.dbc layout requires 234.");
            spellColumns = resolution.Columns;
        }
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var audit = await new SpellSqlAuditService().AuditAsync(profile, capabilities, inspectedSpell, spellDbc, spellColumns, cancellationToken: cancellationToken);
        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(audit, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        Console.WriteLine($"SPELL\t{audit.SpellId}");
        Console.WriteLine($"EFFECTIVE\t{audit.EffectiveSource}");
        Console.WriteLine($"DBC_RECORD\t{(audit.DbcRecordFound ? "present" : spellDbc is null ? "not supplied" : "missing")}");
        Console.WriteLine($"SPELL_DBC_OVERRIDE\t{(audit.HasFullOverride ? $"present · {audit.OverrideDifferences.Count} effective difference(s)" : "absent")}");
        if (!string.IsNullOrWhiteSpace(audit.OverrideComparisonWarning)) Console.WriteLine($"WARNING\t{audit.OverrideComparisonWarning}");
        foreach (var difference in audit.OverrideDifferences)
            Console.WriteLine($"DIFF\t{difference.FieldIndex}\t{difference.DbcField}\t{difference.SqlColumn}\tDBC={Convert.ToString(difference.DbcValue, System.Globalization.CultureInfo.InvariantCulture)}\tSQL={Convert.ToString(difference.SqlValue, System.Globalization.CultureInfo.InvariantCulture)}");
        foreach (var row in audit.RelatedRows)
            Console.WriteLine($"RELATED\t{row.Table}\t{row.Relationship}\t{string.Join(',', row.MatchedColumns)}\t{row.Display}");
        Console.WriteLine($"COVERAGE\t{audit.CheckedTables.Count} checked\t{audit.MissingTables.Count} unavailable\t{audit.RelatedRows.Count} related row(s)");
        foreach (var missing in audit.MissingTables) Console.WriteLine($"MISSING\t{missing}");
        return 0;
    }
    if (operation.Equals("reference-search", StringComparison.OrdinalIgnoreCase) && args.Length >= 7 && Enum.TryParse<ReferenceDomain>(args[5], true, out var referenceDomain))
    {
        if (referenceDomain is not (ReferenceDomain.Spell or ReferenceDomain.Item or ReferenceDomain.Creature or ReferenceDomain.Quest or ReferenceDomain.GameObject))
            return Fail("CLI reference-search domains are spell, item, creature, quest, and gameobject. DBC-only lookup domains are available through the guided desktop picker.");
        var searchOptions = args[7..]; var dbcOption = Option(searchOptions, "--dbc="); var json = searchOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var limitText = Option(searchOptions, "--limit=") ?? "250";
        var unknown = searchOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown reference-search option: {unknown[0]}");
        if (!int.TryParse(limitText, out var referenceLimit) || referenceLimit is < 1 or > 1000) return Fail("--limit must be from 1 to 1000.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var pages = new List<ReferenceLookupPage>();
        pages.Add(await new ReferenceLookupService().SearchSqlAsync(profile, capabilities, referenceDomain, args[6], referenceLimit, cancellationToken));
        if (referenceDomain == ReferenceDomain.Spell && !string.IsNullOrWhiteSpace(dbcOption))
        {
            var dbcPath = Directory.Exists(dbcOption) ? Path.Combine(dbcOption, "Spell.dbc") : dbcOption;
            if (!File.Exists(dbcPath)) return Fail($"Spell.dbc was not found: {Path.GetFullPath(dbcPath)}");
            var dbc = WdbcFile.Load(dbcPath); var resolution = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Spell", dbc.FieldCount);
            if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch) return Fail($"{dbcPath} is not the 234-field WotLK build-12340 Spell.dbc layout.");
            pages.Add(ReferenceLookupService.SearchDbc(referenceDomain, dbc, resolution.Columns, 0, 136, args[6], referenceLimit, 39, 3));
        }
        var result = ReferenceLookupService.Merge(referenceDomain, args[6], referenceLimit, pages.ToArray());
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            foreach (var entry in result.Entries) Console.WriteLine($"{entry.Id}\t{entry.Name}\t{entry.Source}\t{entry.Details}");
            Console.Error.WriteLine($"Reference search: {result.Entries.Count:N0} {referenceDomain} result(s) from {string.Join(" + ", result.Sources)}.{(result.HasMore ? " Refine the query or raise --limit." : string.Empty)}");
        }
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

static int DatabaseHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible db draft-template <domain> <output.json> [--overwrite]\n  wowcrucible db schemas <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db inspect <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db favorites <host> <port> <user> <database> [--search=text] [--verify] [--format=text|json]\n  wowcrucible db rows <host> <port> <user> <database> <table> [--search=text] [--filter=column=value] [--sort=column] [--descending] [--offset=N] [--limit=N] [--format=text|json]\n  wowcrucible db pet-curve <host> <port> <user> <database> <source-creature> <target-creature> [--levels=1-80] [--health=1] [--mana=1] [--armor=1] [--attributes=1] [--damage=1] [--output=curve.sql] [--overwrite] [--format=text|json] [--apply] [--update-existing]\n  wowcrucible db table-admin <host> <port> <user> <database> <table> [--format=text|json]\n  wowcrucible db process-list <host> <port> <user> <database> [--format=text|json]\n  wowcrucible db user-list <host> <port> <user> <database> [--format=text|json]\n  wowcrucible db account <host> <port> <login> <database> grants <account-user> <account-host> [--format=text|json]\n  wowcrucible db account <host> <port> <login> <database> <create|password|lock|unlock|drop> <account-user> <account-host> [--locked] [--apply] [--new-password-env=NAME]\n  wowcrucible db account <host> <port> <login> <database> <grant|revoke> <account-user> <account-host> <privilege[,privilege]> [--global|--table=NAME] [--grant-option] [--apply]\n  wowcrucible db join <host> <port> <user> <database> <relationship-name> [--type=INNER|LEFT|RIGHT] [--limit=N] [--run] [--format=text|json]\n  wowcrucible db index <host> <port> <user> <database> <table> create <name> <column[,column]> [--unique] [--apply]\n  wowcrucible db index <host> <port> <user> <database> <table> drop <name> [--apply]\n  wowcrucible db query <host> <port> <user> <database> <statement.sql> [--output=result.csv|jsonl] [--format=csv|jsonl] [--overwrite] [--write] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db export <host> <port> <user> <database> <table> <output> [--format=csv|jsonl] [--overwrite] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db import <host> <port> <user> <database> <table> <input.csv> [--apply] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db dependency-snapshot <host> <port> <user> <database> <table> <output.json> --key=column=value [--key=column=value]... [--limit=N] [--overwrite]\n  wowcrucible db content-plan <host> <port> <user> <database> <domain> <draft.json> [--output=plan.sql] [--overwrite] [--apply] [--update] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db snapshot <host> <port> <user> <database> <output.crucible-db-snapshot> [--password-env=NAME] [--ssl=Preferred] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]\n  wowcrucible db snapshot-inspect <snapshot-file> [--quick]\n  wowcrucible db recovery-audit <legacy-snapshot> <output.crucible-db-audit> [--baseline=stock-snapshot] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]\n  wowcrucible db recovery-inspect <audit-file> [--quick]\n  wowcrucible db item-audit <host> <port> <user> <database> [--password-env=NAME] [--dbc=folder] [--output=report.json]\n  wowcrucible db item-inspect <host> <port> <user> <database> <item-id> [--password-env=NAME] [--dbc=folder]\n  wowcrucible db item-clone <host> <port> <user> <database> <source-id> <new-id> [--suffix=\" Variant\"] [--itemset=ID]\n  wowcrucible db spell-inspect <host> <port> <user> <database> <spell-id> [--password-env=NAME] [--dbc=Spell.dbc|folder] [--format=text|json]\n  wowcrucible db reference-search <host> <port> <user> <database> <spell|item|creature|quest|gameobject> <id-or-name> [--password-env=NAME] [--dbc=Spell.dbc|folder] [--limit=N] [--format=text|json]\n\nDomains: creature, gameobject, quest, gossip-menu, gossip-option, npc-text, trainer, trainer-spell, trainer-creature, legacy-trainer-spell, pet-level-stats, pet-name-part, pet-name-locale, spell-pet-aura, condition, smartai.\n\nDraft templates and content-plan provide portable complete-field authoring automation. content-plan is dry-run by default; --apply inserts, while --apply --update updates exactly one primary row and inserts collision-free children in one transaction. pet-curve is dry-run by default, clones an existing complete level curve while scaling named stat families, preserves custom columns, inserts only missing levels with --apply, and requires the additional --update-existing acknowledgement to replace exact existing target levels. schemas lists every database accessible to the login without changing it. rows is a read-only complete-column table browser with broad search, exact filters, sorting, and bounded paging. table-admin and process-list are read-only. user-list and account grants are permission-aware metadata reads. account mutations and index changes are exact dry-run plans unless --apply is explicit; new account passwords come only from a separate environment variable and are redacted from previews. join is a dry-run SQL preview unless --run is explicit and remains SELECT-only. dependency-snapshot is SELECT-only and captures a complete primary row plus exact recognized incoming/outgoing rows; it is review data, never executable SQL. Snapshot capture is SELECT-only and excludes known auth/character runtime state by default. query reads SQL from a file so statements and secrets do not need to enter shell history; read results can be exported atomically as CSV/JSONL, while --write is explicit and cannot use result-output switches. import is a dry-run unless --apply is present, is INSERT-only, and rolls back the complete CSV on any duplicate/error. export streams the complete table. item-inspect explains accepted and rejected SQL/DBC acquisition evidence for one exact item ID. spell-inspect reports whether file Spell.dbc or a full spell_dbc row is server-effective, compares every field AzerothCore consumes, and locates recognized related SQL rows; JSON includes complete row values. reference-search provides the same merged ID/name lookup used by guided editors. recovery-audit is completely offline: with a baseline it records baseline-to-legacy deltas; without one it labels rows unattributed candidates. No recovery audit is executable SQL, no-PK tables are blocked from row inference, and removals are never implicitly approved. --include-sensitive is an explicit override. Connection passwords are read from WOW_CRUCIBLE_DB_PASSWORD by default and are never accepted as command arguments.", code);

static SqlDatabaseObjectType ParseDatabaseObjectType(string value) => value.Trim().ToLowerInvariant() switch
{
    "view" or "views" => SqlDatabaseObjectType.View,
    "trigger" or "triggers" => SqlDatabaseObjectType.Trigger,
    "procedure" or "procedures" => SqlDatabaseObjectType.Procedure,
    "function" or "functions" => SqlDatabaseObjectType.Function,
    "event" or "events" => SqlDatabaseObjectType.Event,
    _ => throw new ArgumentException($"Unknown database object type '{value}'. Use view, trigger, procedure, function, or event.")
};

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
    if (args.Length == 0) return DbcHelp(2);
    if (args[0] is "help" or "--help" or "-h" || args.Length > 1 && args[1] is "--help" or "-h") return DbcHelp();
    if(args is ["dbd-info",var dbdPath,var dbdBuildText,..var dbdOptions]&&int.TryParse(dbdBuildText,out var dbdBuild))
    {
        var json=dbdOptions.Contains("--format=json",StringComparer.OrdinalIgnoreCase);var unknown=dbdOptions.Where(value=>!value.Equals("--format=json",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=text",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)return Fail($"Unknown dbd-info option: {unknown[0]}");var definition=DbdSchemaService.Load(dbdPath);var layout=definition.ForBuild(dbdBuild)??throw new KeyNotFoundException($"No layout covers build {dbdBuild:N0}.");var columns=DbdSchemaService.ResolveColumns(definition,dbdBuild);
        if(json)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{definition.TableName,Build=dbdBuild,LogicalColumns=definition.Columns.Values,layout.Builds,layout.LayoutHashes,layout.Comments,layout.Fields,PhysicalColumns=columns},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));else{Console.WriteLine($"TABLE\t{definition.TableName}\nBUILD\t{dbdBuild}\nLAYOUTS\t{definition.Layouts.Count}\nLOGICAL_COLUMNS\t{definition.Columns.Count}\nPHYSICAL_COLUMNS\t{columns.Count}\nBUILD_RANGES\t{string.Join(" | ",layout.Builds.Select(range=>range.Raw))}\nLAYOUT_HASHES\t{string.Join(",",layout.LayoutHashes)}");foreach(var column in columns)Console.WriteLine($"FIELD\t{column.Index}\t{column.Offset}\t{column.Size}\t{column.Type}\t{column.Name}\t{(column.IsIndex?"ID":"")}");}return 0;
    }
    if(args is ["schema-audit",var definitionsRoot,var auditDbcRoot,var auditBuildText,..var auditOptions]&&int.TryParse(auditBuildText,out var auditBuild))
    {
        var xml=Option(auditOptions,"--xml=");var json=auditOptions.Contains("--format=json",StringComparer.OrdinalIgnoreCase);var unknown=auditOptions.Where(value=>!value.StartsWith("--xml=",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=json",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=text",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--only-problems",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)return Fail($"Unknown schema-audit option: {unknown[0]}");var summary=DbdSchemaService.Audit(definitionsRoot,auditDbcRoot,auditBuild,xml);
        if(json)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));else{Console.WriteLine($"BUILD\t{summary.Build}\nTABLES\t{summary.Rows.Count}\nMATCHES\t{summary.Matches}\nEMPTY_PLACEHOLDERS\t{summary.EmptyPlaceholders}\nPROBLEMS\t{summary.Failures}");foreach(var row in summary.Rows.Where(row=>!auditOptions.Contains("--only-problems",StringComparer.OrdinalIgnoreCase)||row.Status is not DbdAuditStatus.Match and not DbdAuditStatus.EmptyPlaceholder))Console.WriteLine($"{row.Status.ToString().ToUpperInvariant()}\t{row.Table}\tWDBC={row.ActualFields}\tDBD={row.DbdFields?.ToString()??"-"}\tXML={row.XmlFields?.ToString()??"-"}\t{row.Message}");}return summary.Failures==0?0:3;
    }
    if (args is ["spell-tooltip", var tooltipDbc, .. var tooltipIds] && tooltipIds.Length > 0)
    {
        var json=tooltipIds.Contains("--format=json",StringComparer.OrdinalIgnoreCase);var tooltipIdTexts=tooltipIds.Where(value=>!value.StartsWith("--",StringComparison.Ordinal)).ToArray();var unknown=tooltipIds.Where(value=>value.StartsWith("--",StringComparison.Ordinal)&&!value.Equals("--format=json",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=text",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)return Fail($"Unknown spell-tooltip option: {unknown[0]}");
        var catalog=SpellTooltipService.Load(tooltipDbc);var records=tooltipIdTexts.Select(value=>uint.TryParse(value,out var id)?catalog.Records.GetValueOrDefault(id):throw new ArgumentException($"Invalid spell ID: {value}")).ToArray();if(json)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(records,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));else foreach(var record in records)Console.WriteLine(record is null?"MISSING":$"SPELL\t{record.Id}\t{record.Name}\t{record.Subtext}\nDESCRIPTION\t{record.Description}\nAURA\t{record.AuraDescription}");return records.All(record=>record is not null)?0:3;
    }
    if (args is ["item-display", var displayDbc, var displaySchema, var displayIdText, .. var displayOptions] && uint.TryParse(displayIdText, out var displayId))
    {
        var itemClass = ParseIntOption(displayOptions, "--class=", 0); var subclass = ParseIntOption(displayOptions, "--subclass=", 0); var inventory = ParseIntOption(displayOptions, "--inventory=", 0);
        var assets = Option(displayOptions, "--assets="); var json = displayOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = displayOptions.Where(option => !option.StartsWith("--class=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--subclass=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--inventory=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--assets=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-display option: {unknown[0]}");
        var result = ItemDisplayInfoService.Resolve(displayDbc, displaySchema == "-" ? null : displaySchema, displayId, itemClass, subclass, inventory, assets);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"DISPLAY\t{result.Id}\nMODEL\t{string.Join(" | ", result.ModelNames.Where(value => value.Length > 0))}\nICON\t{string.Join(" | ", result.InventoryIcons.Where(value => value.Length > 0))}\nGEOSETS\t{string.Join(",", result.GeosetGroups)}\nHELMET_VIS\t{string.Join(",", result.HelmetGeosetVisibility)}\nFLAGS\t0x{result.Flags:X8}\nSPELL_VISUAL\t{result.SpellVisualId}\nITEM_VISUAL\t{result.ItemVisualId}\nPARTICLE_COLOR\t{result.ParticleColorId}\nSOUND_GROUP\t{result.GroupSoundIndex}");
            foreach (var asset in result.Assets) Console.WriteLine($"ASSET\t{asset.Kind}\t{asset.Slot}\t{asset.Name}\t{string.Join(" | ", asset.ClientPaths)}\t{(asset.ExistingPaths.Count == 0 ? "MISSING" : string.Join(" | ", asset.ExistingPaths))}");
        }
        return 0;
    }
    if (args is ["item-equipped", var equipmentDbc, var equipmentSchema, var equipmentIdText, var baseSkin, var outputAtlas, .. var equipmentOptions] && uint.TryParse(equipmentIdText, out var equipmentId))
    {
        var itemClass = ParseIntOption(equipmentOptions, "--class=", 4); var subclass = ParseIntOption(equipmentOptions, "--subclass=", 0); var inventory = ParseIntOption(equipmentOptions, "--inventory=", 0);
        var assets = Option(equipmentOptions, "--assets=") ?? throw new ArgumentException("item-equipped requires --assets=processed-library."); var requestedSource = Option(equipmentOptions, "--source=");
        var unknown = equipmentOptions.Where(option => !option.StartsWith("--class=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--subclass=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--inventory=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--assets=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-equipped option: {unknown[0]}");
        var display = ItemDisplayInfoService.Resolve(equipmentDbc, equipmentSchema == "-" ? null : equipmentSchema, equipmentId, itemClass, subclass, inventory, assets);
        var sources = ItemEquipmentPreviewService.FindWearSources(display); if (sources.Count == 0) throw new FileNotFoundException("No extracted wear textures for this display were found in the processed asset library.");
        var source = requestedSource is null ? sources[0] : sources.FirstOrDefault(value => value.Source.Equals(requestedSource, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"Wear source '{requestedSource}' was not found. Available: {string.Join(", ", sources.Select(value => value.Source))}");
        var preview = ItemEquipmentPreviewService.Compose(baseSkin, display, inventory, source); BlpTextureService.WritePng(outputAtlas, preview.Atlas, equipmentOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase));
        Console.WriteLine($"DISPLAY\t{display.Id}\nSOURCE\t{source.Source}\nATLAS\t{Path.GetFullPath(outputAtlas)}\nWEAR_SLOTS\t{string.Join(",", preview.AppliedSlots)}\nMISSING_SLOTS\t{string.Join(",", preview.MissingSlots)}\nGEOSETS\t{string.Join(",", preview.Geosets.GroupVariants.Select(pair => $"{pair.Key}:{pair.Value}"))}"); return 0;
    }
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
        var resolution = ResolveClientTableSchema(rowsFile, rowsSchemaPath, tableName);
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
    if (args is ["export", var exportPath, var exportSchemaPath, var exportOutput, .. var exportOptions])
    {
        var formatText = Option(exportOptions, "--format=") ?? Path.GetExtension(exportOutput).ToLowerInvariant() switch { ".csv" => "csv", ".json" => "json", _ => "jsonl" };
        var format = formatText.ToLowerInvariant() switch { "csv" => DbcRowExportFormat.Csv, "json" => DbcRowExportFormat.Json, "jsonl" or "json-lines" => DbcRowExportFormat.JsonLines, _ => throw new ArgumentException("--format must be csv, json, or jsonl.") };
        var columns = exportOptions.Where(option => option.StartsWith("--column=", StringComparison.OrdinalIgnoreCase)).Select(option => option[9..])
            .Concat((Option(exportOptions, "--columns=") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        var keys = exportOptions.Where(option => option.StartsWith("--id=", StringComparison.OrdinalIgnoreCase)).Select(option => option[5..])
            .Concat((Option(exportOptions, "--ids=") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => uint.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"Invalid DBC export ID: {value}")).ToArray();
        var rawStrings = exportOptions.Contains("--raw-string-offsets", StringComparer.OrdinalIgnoreCase); var overwrite = exportOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = exportOptions.Where(option => !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--column=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--columns=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--id=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--raw-string-offsets", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown DBC export option: {unknown[0]}");
        var exportFile = WdbcFile.Load(exportPath); var table = Path.GetFileNameWithoutExtension(exportPath); var resolution = ResolveClientTableSchema(exportFile, exportSchemaPath, table);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(table, exportFile.FieldCount, resolution));
        var result = DbcRowExportService.Export(exportFile, resolution, exportOutput, new(format, columns, keys, rawStrings, overwrite));
        Console.Error.WriteLine($"Exported {result.ExportedRows:N0}/{result.SourceRows:N0} {table} row(s), {result.Columns.Count:N0} output columns, decoded strings={!rawStrings}: {result.OutputPath}"); return 0;
    }
    if (args is ["import", var importPath, var importSchemaPath, var importDataPath, .. var importOptions])
    {
        var formatText = Option(importOptions, "--format=") ?? DbcRowImportService.InferFormat(importDataPath).ToString();
        var format = formatText.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "csv" => DbcRowImportFormat.Csv,
            "json" => DbcRowImportFormat.Json,
            "jsonl" or "jsonlines" or "ndjson" => DbcRowImportFormat.JsonLines,
            _ => throw new ArgumentException("--format must be csv, json, or jsonl.")
        };
        var output = Option(importOptions, "--output="); var append = importOptions.Contains("--append", StringComparer.OrdinalIgnoreCase);
        var rawStrings = importOptions.Contains("--raw-string-offsets", StringComparer.OrdinalIgnoreCase); var overwrite = importOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var jsonReport = importOptions.Contains("--report=json", StringComparer.OrdinalIgnoreCase);
        var unknown = importOptions.Where(option => !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--append", StringComparison.OrdinalIgnoreCase) && !option.Equals("--raw-string-offsets", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--report=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--report=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown DBC import option: {unknown[0]}");
        var importFile = WdbcFile.Load(importPath); var table = Path.GetFileNameWithoutExtension(importPath); var resolution = ResolveClientTableSchema(importFile, importSchemaPath, table);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(table, importFile.FieldCount, resolution));
        if (output is not null)
        {
            var fullOutput = Path.GetFullPath(output);
            if (fullOutput.Equals(Path.GetFullPath(importDataPath), StringComparison.OrdinalIgnoreCase) || fullOutput.Equals(Path.GetFullPath(importSchemaPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("DBC import output cannot replace its structured input or schema definition.");
            if (File.Exists(fullOutput) && !overwrite) throw new IOException($"Import output already exists: {fullOutput}. Use --overwrite explicitly to replace it with a .bak backup.");
        }
        var plan = DbcRowImportService.Preview(importFile, resolution, importDataPath, new(format, append, rawStrings));
        if (jsonReport) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Table = table, plan.InputPath, plan.InputSha256, plan.SourceContentSha256, plan.Format, plan.InputRows, plan.UpdatedRows, plan.AppendedRows, plan.ChangedCells, plan.HasChanges, plan.Warnings, plan.Changes }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"PLAN\t{table}\tinput={plan.InputRows}\tupdated={plan.UpdatedRows}\tappended={plan.AppendedRows}\tcells={plan.ChangedCells}\tformat={plan.Format}");
            foreach (var warning in plan.Warnings) Console.WriteLine($"WARN\t{warning}");
            foreach (var change in plan.Changes.Take(200)) Console.WriteLine($"CHANGE\tinput={change.InputRow}\tkey={change.RecordKey?.ToString() ?? "-"}\trow={change.TargetRow}\t{change.Column}\t{change.Before}\t=>\t{change.After}");
            if (plan.Changes.Count > 200) Console.WriteLine($"MORE\t{plan.Changes.Count - 200:N0} additional cell change(s); use --report=json for the complete plan.");
        }
        if (output is null) { Console.Error.WriteLine($"Dry-run import preview only. No client table changed; add --output=changed{Path.GetExtension(importPath)} to apply this exact plan to a new/explicitly overwritten output."); return 0; }
        var result = DbcRowImportService.Apply(importFile, plan); output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!); importFile.Save(output, overwrite);
        Console.Error.WriteLine($"Applied structured import atomically: {result.UpdatedRows:N0} updated row(s), {result.AppendedRows:N0} appended row(s), {result.ChangedCells:N0} changed cell(s), {result.ResultRows:N0} result rows. Output: {output}{(overwrite ? $" · previous output backed up to {output}.bak" : string.Empty)}"); return 0;
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
        var resolution = ResolveClientTableSchema(findFile, findSchemaPath, tableName);
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
        var resolution = ResolveClientTableSchema(sample, schemaPath, tableName);
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
        var resolution = ResolveClientTableSchema(sample, promotionSchemaPath, tableName);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        DbcPromotionService.Apply(promotionBasePath, promotionOverridePath, outputPath, resolution.Columns, resolution.KeyStrategy, DbcPromotionService.LoadManifest(manifestPath));
        Console.Error.WriteLine($"Created promoted client table: {Path.GetFullPath(outputPath)}");
        return 0;
    }
    if (args is ["promote", "additions", var additionsBasePath, var additionsOverridePath, var additionsSchemaPath, var additionsManifestPath, var additionsOutputPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(additionsBasePath); var sample = WdbcFile.Load(additionsBasePath);
        var resolution = ResolveClientTableSchema(sample, additionsSchemaPath, tableName);
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
        var baseFile = WdbcFile.Load(cloneBasePath); var sourceFile = WdbcFile.Load(cloneSourcePath); var tableName = baseFile.LogicalTableName;
        if (!baseFile.LogicalTableName.Equals(sourceFile.LogicalTableName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Base and source client-table identities differ.");
        var resolution = ResolveClientTableSchema(sourceFile, cloneSchemaPath, tableName);
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
        var parentResolution = ResolveClientTableSchema(parentSource, parentSchemaPath, parentTable);
        if (parentResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(parentTable, parentSource.FieldCount, parentResolution));
        var childBase = WdbcFile.Load(childBasePath); var childTable = Path.GetFileNameWithoutExtension(childBasePath);
        var childResolution = ResolveClientTableSchema(childBase, childSchemaPath, childTable);
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
        var copyResolution = ResolveClientTableSchema(copySample, copySchemaPath, copyTable);
        if (copyResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(copyTable, copySample.FieldCount, copyResolution));
        DbcRowMutationService.CopyRow(copyBasePath, copySourcePath, copyOutputPath, copyResolution.Columns, copyResolution.KeyStrategy, copySourceId, copyTargetId, copyValues);
        Console.Error.WriteLine($"Copied {copyTable} ID {copySourceId} to additive ID {copyTargetId} with {copyValues.Count:N0} field override(s): {Path.GetFullPath(copyOutputPath)}");
        return 0;
    }
    if (args is ["set-row", var setInputPath, var setSchemaPath, var setIdText, var setOutputPath, .. var setOptions])
    {
        if (!uint.TryParse(setIdText, out var setId)) return Fail("Record ID must be an unsigned integer.");
        var setValues = ParseSetOptions(setOptions); var setSample = WdbcFile.Load(setInputPath); var setTable = Path.GetFileNameWithoutExtension(setInputPath);
        var setResolution = ResolveClientTableSchema(setSample, setSchemaPath, setTable);
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
    Console.WriteLine($"Container\t{file.ContainerKind.ToString().ToUpperInvariant()}"); Console.WriteLine($"Rows\t{file.RowCount}"); Console.WriteLine($"Fields\t{file.FieldCount}"); Console.WriteLine($"RecordBytes\t{file.RecordSize}"); Console.WriteLine($"StringBytes\t{file.StringTableSize}");
    if (file.Db2Metadata is { } db2) Console.WriteLine($"Build\t{db2.Build}\nTableHash\t0x{db2.TableHash:X8}\nTimestamp\t{db2.Timestamp}\nIdRange\t{db2.MinId}..{db2.MaxId}\nLocale\t0x{db2.Locale:X8}\nIndexEntries\t{db2.IndexMap.Count}\nCopyRows\t{db2.CopyRows}\nStructuralMutation\t{file.AllowsStructuralMutation}");
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
                var allFiles = LoadMpqIndex(service, args[1], listFile);
                var files = allFiles.Where(file => (!contentOnly || !file.IsMetadata) && MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(files, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                else foreach (var file in files) Console.WriteLine($"{file.Size}\t{file.CompressedSize}\t{file.ArchivePath}");
                PrintAnonymousMpqWarning(allFiles, listFile);
                return 0;
            }
        case "tree" when args.Length >= 2:
            {
                var options = args[2..]; var folder = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var listFile = Option(options, "--listfile=");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown tree option: {unknown[0]}");
                var allFiles = LoadMpqIndex(service, args[1], listFile); var page = MpqArchiveBrowser.Browse(allFiles, folder);
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(page, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var node in page.Nodes) Console.WriteLine($"{(node.IsFolder ? "DIR" : "FILE")}\t{node.Kind}\t{node.FileCount}\t{node.Size}\t{node.CompressedSize}\t{node.ArchivePath}");
                Console.Error.WriteLine($"{(page.CurrentFolder.Length == 0 ? "MPQ root" : page.CurrentFolder)}: {page.Nodes.Count:N0} direct node(s), {page.RecursiveFiles:N0} recursive file(s), {page.AnonymousFiles:N0} anonymous name(s)."); PrintAnonymousMpqWarning(allFiles, listFile); return 0;
            }
        case "extract-folder" when args.Length >= 4:
            {
                var options = args[4..]; var quiet = options.Any(option => option.Equals("--quiet", StringComparison.OrdinalIgnoreCase)); var listFile = Option(options, "--listfile="); var progressOption = options.FirstOrDefault(option => option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase)); var progressStep = progressOption is null ? 5 : int.Parse(progressOption[11..]);
                if (progressStep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(progressStep), "Progress percentage must be from 1 to 100."); var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown extract-folder option: {unknown[0]}");
                var allFiles = LoadMpqIndex(service, args[1], listFile); var files = MpqArchiveBrowser.SelectFolder(allFiles, args[2]); if (files.Count == 0) return Fail($"MPQ folder not found or empty: {args[2]}"); PrintAnonymousMpqWarning(allFiles, listFile); var timer = Stopwatch.StartNew(); service.Extract(args[1], args[3], files, quiet ? null : new ConsoleProgress(progressStep)); Console.Error.WriteLine($"Extracted {files.Count:N0} recursive file(s) from {args[2]} to {Path.GetFullPath(args[3])} in {timer.Elapsed.TotalSeconds:0.##}s."); return 0;
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
                var allFiles = LoadMpqIndex(service, args[1], listFile);
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
static int DbcHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible dbc info <file.dbc|file.db2>\n  wowcrucible dbc dbd-info <file.dbd> <build> [--format=text|json]\n  wowcrucible dbc schema-audit <definitions-root> <table-folder> <build> [--xml=schema.xml] [--only-problems] [--format=text|json]\n  wowcrucible dbc rows <file.dbc|file.db2> <schema.xml|file.dbd|definitions-folder> <id>...\n  wowcrucible dbc export <file.dbc|file.db2> <schema> <output.csv|json|jsonl> [--format=csv|json|jsonl] [--columns=A,B|--column=Name] [--ids=1,2|--id=N] [--raw-string-offsets] [--overwrite]\n  wowcrucible dbc import <file.dbc|file.db2> <schema> <input.csv|json|jsonl> [--format=csv|json|jsonl] [--append] [--raw-string-offsets] [--output=changed.dbc|db2] [--overwrite] [--report=text|json]\n  wowcrucible dbc find <file.dbc|file.db2> <schema> <column> <value>... [--count|--limit=N]\n  wowcrucible dbc validate <schema.xml> <dbc-folder> [--strict] [--recursive]\n  wowcrucible dbc compare <base> <override> <schema> [--summary]\n  wowcrucible dbc promote apply <base> <override> <schema> <manifest.json> <output>\n  wowcrucible dbc promote additions <base> <override> <schema> <manifest.json> <output>\n  wowcrucible dbc clone-remap where <base> <source> <schema> <column> <value>... --manifest=map.json --output=merged.dbc|db2 [--start-id=N]\n  wowcrucible dbc clone-dependency <parent-source> <parent-merged> <parent-schema> <parent-map.json> <foreign-column> <child-base> <child-source> <child-schema> --child-map=map.json --child-output=child --parent-output=parent\n  wowcrucible dbc copy-row <base> <source> <schema> <source-id> <target-id> <output> [--set=Column=Value]...\n  wowcrucible dbc set-row <input> <schema> <id> <output> --set=Column=Value [...]\n  wowcrucible dbc spell-tooltip <Spell.dbc> <spell-id>... [--format=text|json]\n  wowcrucible dbc item-display <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> [--assets=processed-library]\n  wowcrucible dbc item-equipped <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> <base-skin> <output.png> --inventory=N --assets=processed-library [--source=name]\n  wowcrucible dbc itemset inspect <ItemSet.dbc> <schema.xml> <set-id> [--spell=Spell.dbc]\n  wowcrucible dbc itemset clone <ItemSet.dbc> <schema.xml> <output.dbc> <source-set> <new-set> --map=old:new,... [--suffix=\" Variant\"]\n  wowcrucible dbc itemset effects <ItemSet.dbc> <schema.xml> <output.dbc> <set-id> --effect=required-items:spell-id [...]\n\nFor WDB2, <schema> is the matching .dbd file or WoWDBDefs definitions folder. WDB5/WDB6/WDC are not yet supported.", code);
static int MpqHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json] [--listfile=paths.txt]\n  wowcrucible mpq tree <archive.mpq> [folder] [--format=text|json] [--listfile=paths.txt]\n  wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N] [--listfile=paths.txt]\n  wowcrucible mpq extract-folder <archive.mpq> <internal-folder> <destination> [--quiet|--progress=N] [--listfile=paths.txt]\n  wowcrucible mpq create <archive.mpq> <files/folders...>\n  wowcrucible mpq update <archive.mpq> <files/folders...>\n  wowcrucible mpq merge <output.mpq> <source-a.mpq> <source-b.mpq> [...] [--conflicts=block|earlier|later] [--listfile=paths.txt]", code);
static int ToolingHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible tools commands [search words...] [--format=text|json]\n  wowcrucible tools inventory [workspace-root] [--format=text|json] [--unassigned-only] [--no-missing]\n\nThe command catalog is shared with the desktop Ctrl+K palette, so scripts and the UI use the same searchable vocabulary. A command search with no matches returns exit code 3.\n\nWithout an inventory path, Crucible searches upward from the executable for the shared wow-edits workspace. Any new unassigned directory returns exit code 3 so automation cannot silently claim complete tool coverage.", code);
static void PrintAnonymousMpqWarning(IReadOnlyList<MpqFileEntry> files, string? listFile)
{
    var anonymous = files.Count(file => ClientArchiveIndexService.IsAnonymous(file.ArchivePath));
    if (anonymous == 0) return;
    Console.Error.WriteLine($"WARNING: Archive opened successfully, but {anonymous:N0} file name(s) are unresolved StormLib placeholders.{(string.IsNullOrWhiteSpace(listFile) ? " Supply --listfile=paths.txt to recover known paths." : " The supplied listfile did not resolve every path.")}");
}
static IReadOnlyList<MpqFileEntry> LoadMpqIndex(PatchArchiveService service, string archive, string? listFile)
{
    var result = MpqArchiveIndexCache.LoadOrCreate(archive, listFile, () => service.ListFiles(archive, "*", listFile)); Console.Error.WriteLine($"MPQ index: {(result.Cached ? "cache hit" : "read archive and cached")} · {result.Entries.Count:N0} entries."); return result.Entries;
}
static int GroupHelp(string message, int code)
{
    if (message.Contains("wowcrucible asset texture-info", StringComparison.Ordinal))
        message = message.Replace("Usage:\n", "Usage:\n  wowcrucible asset map-info <file.adt|wdt|wdl> [--cells] [--format=text|json]\n  wowcrucible asset adt-height-plan <input.adt> <delta> <x:y,x:y|all> <plan.json> [--overwrite]\n  wowcrucible asset adt-height-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-brush-plan <input.adt> <center-x:center-y> <radius> <strength> <plan.json> [--mode=raise-lower|flatten|smooth|noise] [--target-height=N] [--seed=N] [--falloff=linear|smooth|constant] [--overwrite]\n  wowcrucible asset adt-brush-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-texture-info <input.adt> [--cells] [--format=text|json]\n  wowcrucible asset adt-texture-plan <input.adt> <layer-slot> <texture-id> <x:y,x:y|all> <plan.json> [--overwrite]\n  wowcrucible asset adt-texture-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-texture-add-plan <input.adt> <client-texture.blp> <x:y,x:y> <plan.json> [--encoding=auto|packed-4-bit|big-8-bit|rle-8-bit] [--initial-alpha=0] [--overwrite]\n  wowcrucible asset adt-texture-add-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-alpha-info <input.adt> [--cells] [--format=text|json]\n  wowcrucible asset adt-alpha-plan <input.adt> <layer-slot> <center-x:center-y> <radius> <target-alpha> <opacity> <x:y,x:y|all> <plan.json> [--falloff=linear|smooth|constant] [--overwrite]\n  wowcrucible asset adt-alpha-apply <plan.json> <output.adt> [--overwrite]\n", StringComparison.Ordinal);
    message = message.Replace("map-info <file.adt|wdt|wdl> [--cells] [--format=text|json]", "map-info <file.adt|wdt|wdl> [--cells] [--placements] [--format=text|json]", StringComparison.Ordinal);
    if (message.Contains("wowcrucible db query", StringComparison.Ordinal))
        message += "\n\nDatabase objects:\n  wowcrucible db objects <host> <port> <user> <database> [--type=view|trigger|procedure|function|event] [--format=text|json]\n  wowcrucible db object-show <host> <port> <user> <database> <type> <name> [--format=text|json]\n  wowcrucible db object-export <host> <port> <user> <database> <output.sql> [--overwrite]\n  wowcrucible db object-drop <host> <port> <user> <database> <type> <name> [--apply]\n  wowcrucible db view-set <host> <port> <user> <database> <name> <select.sql> [--apply]\n  wowcrucible db event-state <host> <port> <user> <database> <name> <enable|disable> [--apply]\n\nTarget-bound synchronization:\n  wowcrucible db sync-plan <host> <port> <user> <database> <verified-audit> <plan.json> [--include=glob]... [--include-removals] [--auto-remap] [--remap-start=ID] [--maximum=N] [--overwrite]\n  wowcrucible db sync-inspect <plan.json> [--sql=preview.sql] [--overwrite]\n  wowcrucible db sync-apply <host> <port> <user> <database> <plan.json> <receipt.json> [--apply] [--overwrite]\n  wowcrucible db sync-rollback <host> <port> <user> <database> <receipt.json> [--apply]\n\nObject mutations and synchronization apply/rollback are dry-run unless --apply is explicit. Guided views accept exactly one independently validated SELECT; synchronization requires a verified baseline comparison, exact primary keys, target preimage matches, and a rollback receipt. Automatic collision remapping is opt-in and every source-to-target ID plus rewritten recognized reference is printed for review.\n\nRead-only query batches: add --batch to execute up to 32 semicolon-delimited SELECT/SHOW/DESCRIBE/EXPLAIN statements from one SQL file. Use --batch-format=text|json for independently shaped, labeled result sets. Batches reject --write and single-result --output switches; SELECT file output is always blocked.";
    if (code == 0) Console.WriteLine(message); else Console.Error.WriteLine(message); return code;
}

static int Help()
{
    Console.WriteLine("WoW Crucible CLI\n\nGlobal options:\n  --devbug   mirror terminal output and diagnostics to Logs\\Debug (newest 3 CLI sessions retained)\n\nCommand groups (run wowcrucible <group> --help for full syntax):\n  asset     inspect/preview models and build resumable extracted/PNG asset libraries\n  project   create portable content projects and reserve collision-checked IDs\n  tools     search native commands and inventory the local legacy-tool corpus\n  client    install patches, clear cache, index/extract clients, and plan fusion\n  server    detect installed cores, audit DBC/SQL bindings, and stage client changes\n  db        inspect schemas, recover legacy SQL changes offline, audit items, and clone complete items\n  dbc       inspect/edit/validate/compare/promote DBCs and author item sets\n  mpq       list, extract, create, merge, and safely update small patch archives\n  manifest  define, verify, and build tiny reviewable patch MPQs\n\nExamples:\n  wowcrucible --devbug mpq list patch-H.MPQ\n  wowcrucible tools commands \"cut items\"\n  wowcrucible tools inventory --unassigned-only\n  wowcrucible project --help\n  wowcrucible db --help\n  wowcrucible dbc --help\n  wowcrucible asset --help\n\nThe full copy-paste guide ships as docs\\CLI-REFERENCE.md beside the application.");
    return 0;
}

static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

static string? Option(IEnumerable<string> options, string prefix) => options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
static int ParseIntOption(IEnumerable<string> options, string prefix, int fallback)
{
    var value = options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return value is null ? fallback : int.TryParse(value[prefix.Length..], out var parsed) ? parsed : throw new ArgumentException($"{prefix[..^1]} must be an integer.");
}
static string FormatValues(IReadOnlyDictionary<string, object?> values) => values.Count == 0 ? "<missing>" : string.Join(", ", values.Select(pair => $"{pair.Key}={SqlCell(pair.Value)}"));
static string SqlCell(object? value) => value switch
{
    null => string.Empty,
    byte[] bytes => "0x" + Convert.ToHexString(bytes),
    DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture),
    IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
};
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

static DbcSchemaResolution ResolveClientTableSchema(WdbcFile file, string schemaPath, string tableName)
{
    var isDbd = Directory.Exists(schemaPath) || Path.GetExtension(schemaPath).Equals(".dbd", StringComparison.OrdinalIgnoreCase);
    if (!isDbd) return DbcSchemaCatalog.Load(schemaPath).ResolveColumns(tableName, file.FieldCount);
    tableName = file.LogicalTableName;
    var build = file.Db2Metadata?.Build ?? throw new InvalidOperationException("A DBD schema path currently requires a WDB2 file carrying its client build. Use the matching XML definition for WDBC.");
    var definition = Directory.Exists(schemaPath) ? Path.Combine(Path.GetFullPath(schemaPath), tableName + ".dbd") : Path.GetFullPath(schemaPath);
    if (!File.Exists(definition)) throw new FileNotFoundException($"No DBD definition exists for {tableName}.", definition);
    return DbdSchemaService.ResolveFile(definition, build, file.FieldCount, file.RecordSize);
}

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

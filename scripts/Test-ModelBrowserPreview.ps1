#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DesktopDirectory,
    [Parameter(Mandatory = $true)][string]$ModelPath,
    [Parameter(Mandatory = $true)][string]$ScreenshotPath
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or [Environment]::Version.Major -lt 10) { throw 'Use PowerShell 7.6+ on Windows with .NET 10+.' }
$directory = (Get-Item -LiteralPath $DesktopDirectory).FullName
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
foreach ($library in @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
    $path = Join-Path $directory $library
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $path = Join-Path $directory "runtimes\win-$architecture\native\$library" }
    [void][Runtime.InteropServices.NativeLibrary]::Load($path)
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'Avalonia.Controls.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'Avalonia.Desktop.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'Avalonia.Themes.Fluent.dll'))
$desktop = [Reflection.Assembly]::LoadFrom((Join-Path $directory 'WoWCrucible.Desktop.dll'))
[void][Avalonia.AppBuilderDesktopExtensions]::UsePlatformDetect([Avalonia.AppBuilder]::Configure[WoWCrucible.Desktop.App]()).SetupWithoutStarting()
$settings = [Activator]::CreateInstance($desktop.GetType('WoWCrucible.Desktop.DesktopSettings', $true), $true)
$type = $desktop.GetType('WoWCrucible.Desktop.ModelBrowserView', $true)
$view = [Activator]::CreateInstance($type, [object[]]@($settings))
$window = [Avalonia.Controls.Window]::new()
$window.Content = $view
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
function Field([string]$name) { return ,$type.GetField($name, $flags).GetValue($view) }
function SetField([string]$name, $value) { $type.GetField($name, $flags).SetValue($view, $value) }
function InvokeView([string]$name) { [void]$type.GetMethod($name, $flags).Invoke($view, @()) }
$source = $null
$bitmap = $null
try {
    # Populate the view in memory; do not run OpenAsync, write settings, or open a desktop window.
    $catalog = [WoWCrucible.Core.ModelBrowserCatalogService]::Scan((Split-Path -LiteralPath $ModelPath), [Threading.CancellationToken]::None)
    $entry = $catalog.Models | Where-Object FilePath -EQ $ModelPath | Select-Object -First 1
    if (!$entry) { throw 'Model not discovered.' }
    $source = [WoWCrucible.Core.ModelBrowserSource]::new($entry, $catalog)
    $geometry = [WoWCrucible.Core.M2PreviewGeometryService]::LoadForViewing($source, $null, [WoWCrucible.Core.M2PreviewVisibilityMode]::AllGeosets, $null)
    SetField '_catalog' $catalog
    SetField '_current' $entry
    SetField '_source' $source
    SetField '_fullGeometry' $geometry
    $bindings = [WoWCrucible.Core.ModelBrowserTextureService]::SuggestBindings($source, $geometry)
    foreach ($pair in $bindings.GetEnumerator()) {
        (Field '_bindings').Add($pair.Key, $pair.Value)
        (Field '_decoded').Add($pair.Key, [WoWCrucible.Core.ModelBrowserTextureService]::Decode($source, $pair.Value))
    }
    if ((Field '_decoded').Count -eq 0) { throw 'No real model textures resolved.' }
    foreach ($index in [WoWCrucible.Core.M2GeosetCatalog]::BrowserDefaults($geometry.Submeshes)) { [void](Field '_selectedGeosets').Add($index) }
    (Field '_root').Text = $catalog.Root
    (Field '_modelTitle').Text = $entry.Name
    InvokeView 'Filter'
    InvokeView 'BuildGeosets'
    InvokeView 'BuildTextures'
    InvokeView 'ShowGeometry'
    (Field '_preview').SetDecodedTextures((Field '_decoded'))
    $texturePane = Field '_textures'
    $materialList = $texturePane.GetType().GetField('_materials', $flags).GetValue($texturePane)
    $visibleTextures = (Field '_preview').GetType().GetField('_geometry', $flags).GetValue((Field '_preview')).UsedTextureDefinitionIndices.Count
    if ($materialList.ItemCount -lt $visibleTextures) { throw "Visible unassigned materials are missing from the texture picker: $($materialList.ItemCount) / $visibleTextures." }
    foreach ($row in $materialList.ItemsSource) { [void]$materialList.ItemTemplate.Build($row) }
    [void]$materialList.ItemTemplate.Build($null)
    $list = Field '_models'
    [void]$list.ItemTemplate.Build($null)
    [void]$list.ItemTemplate.Build($entry)
    $window.Width = 1440; $window.Height = 900
    [void]$window.ApplyTemplate()
    $window.Measure([Avalonia.Size]::new(1440, 900))
    $window.Arrange([Avalonia.Rect]::new(0, 0, 1440, 900))
    $view.Width = 1440; $view.Height = 900
    $view.Measure([Avalonia.Size]::new(1440, 900))
    $view.Arrange([Avalonia.Rect]::new(0, 0, 1440, 900))
    [Avalonia.Threading.Dispatcher]::UIThread.RunJobs()
    $view.Measure([Avalonia.Size]::new(1440, 900))
    $view.Arrange([Avalonia.Rect]::new(0, 0, 1440, 900))
    $bitmap = [Avalonia.Media.Imaging.RenderTargetBitmap]::new([Avalonia.PixelSize]::new(1440, 900), [Avalonia.Vector]::new(96, 96))
    $bitmap.Render($view)
    $bitmap.Save($ScreenshotPath)
    $firstFrame = (Get-FileHash -LiteralPath $ScreenshotPath -Algorithm SHA256).Hash
    $preview = Field '_preview'
    $preview.GetType().GetMethod('Scrub', $flags).Invoke($preview, [object[]]@([double]500))
    $bitmap.Render($view)
    $bitmap.Save($ScreenshotPath)
    if ((Get-FileHash -LiteralPath $ScreenshotPath -Algorithm SHA256).Hash -eq $firstFrame) { throw 'Animation did not change the rendered frame.' }
    $renderWatch = [Diagnostics.Stopwatch]::StartNew()
    for ($frame = 0; $frame -lt 10; $frame++) {
        $preview.GetType().GetMethod('Scrub', $flags).Invoke($preview, [object[]]@([double](500 + $frame * 33)))
        $bitmap.Render($view)
    }
    $renderWatch.Stop()
    Write-Output "Render timing: $([math]::Round($renderWatch.Elapsed.TotalMilliseconds / 10, 1)) ms per full browser animation frame."
    $pixels = [SkiaSharp.SKBitmap]::Decode($ScreenshotPath)
    try {
        $colors = [Collections.Generic.HashSet[uint32]]::new()
        for ($y = 130; $y -lt 740; $y += 2) {
            for ($x = 480; $x -lt 920; $x += 2) { [void]$colors.Add([uint32]$pixels.GetPixel($x, $y)) }
        }
        if ($colors.Count -lt 100) { throw "Preview is blank or untextured: only $($colors.Count) colors in its central region." }
        Write-Output "PASS model browser render: $($geometry.Vertices.Count) vertices, $((Field '_decoded').Count) textures, $($colors.Count) central colors, animated pixel change, empty/rebound list templates. Screenshot: $ScreenshotPath"
    } finally { $pixels.Dispose() }
} finally {
    if ($bitmap) { $bitmap.Dispose() }
    $view.Dispose()
    $window.Close()
    if ($source) { $source.Dispose() }
}

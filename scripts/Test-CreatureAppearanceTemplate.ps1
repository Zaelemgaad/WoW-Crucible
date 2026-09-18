#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$DesktopDirectory)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or [Environment]::Version.Major -lt 10) {
    throw 'Run this UI regression in a fresh PowerShell 7.6+ process on Windows with .NET 10+.'
}
$directory = (Get-Item -LiteralPath $DesktopDirectory).FullName
$desktopPath = Join-Path $directory 'WoWCrucible.Desktop.dll'
if (!(Test-Path -LiteralPath $desktopPath -PathType Leaf)) { throw "Build the Desktop project first: $desktopPath" }

# Load the existing build in memory. No extra output tree, saved settings, or GUI window.
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
foreach ($library in @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
    $path = Join-Path $directory $library
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $path = Join-Path $directory "runtimes\win-$architecture\native\$library" }
    [void][Runtime.InteropServices.NativeLibrary]::Load($path)
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'Avalonia.Controls.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'Avalonia.Desktop.dll'))
[void][Avalonia.AppBuilderDesktopExtensions]::UsePlatformDetect([Avalonia.AppBuilder]::Configure[Avalonia.Application]()).SetupWithoutStarting()
$desktop = [Reflection.Assembly]::LoadFrom($desktopPath)
$settings = [Activator]::CreateInstance($desktop.GetType('WoWCrucible.Desktop.DesktopSettings', $true), $true)
$session = [Activator]::CreateInstance($desktop.GetType('WoWCrucible.Desktop.DesktopWorkspaceSession', $true), [object[]]@($settings))
$view = $null
try {
    $type = $desktop.GetType('WoWCrucible.Desktop.CreatureWorkspaceView', $true)
    $view = [Activator]::CreateInstance($type, [object[]]@($session))
    $list = $type.GetField('_appearanceResults', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($view)
    $template = $list.ItemTemplate
    $empty = $template.Build($null)
    if ($null -ne $empty -and @([Avalonia.VisualTree.VisualExtensions]::GetVisualDescendants($empty) | Where-Object { $_ -is [Avalonia.Controls.TextBlock] -and $_.Text }).Count -gt 0) {
        throw 'An empty recycled row retained appearance text.'
    }

    $entries = @(
        [WoWCrucible.Core.CreatureDisplayCatalogEntry]::new(101, 201, 'Creature\Test\First.m2', 1.5, 2, [string[]]@('FirstSkin.blp'), ''),
        [WoWCrucible.Core.CreatureDisplayCatalogEntry]::new(102, 0, '', 1, 1, [string[]]@(), 'Missing CreatureModelData record'),
        [WoWCrucible.Core.CreatureDisplayCatalogEntry]::new(103, 203, 'Creature\Test\Second.m2', 1, 1, [string[]]@('', ''), '')
    )
    $presenter = [Avalonia.Controls.Presenters.ContentPresenter]::new()
    $presenter.ContentTemplate = $template
    $size = [Avalonia.Size]::new(600, [double]::PositiveInfinity)
    for ($cycle = 0; $cycle -lt 100; $cycle++) {
        foreach ($entry in $entries) {
            $presenter.Content = $entry
            $presenter.UpdateChild()
            $card = $presenter.Child
            if ($null -eq $card) { throw 'A real appearance row was discarded.' }
            $card.Measure($size)
            $text = @([Avalonia.VisualTree.VisualExtensions]::GetVisualDescendants($card) | Where-Object { $_ -is [Avalonia.Controls.TextBlock] } | ForEach-Object Text)
            if ($text.Count -ne 3 -or $text[0] -notlike "Display $($entry.DisplayId)*") {
                throw 'A reused appearance row is blank or shows the previous display ID.'
            }
            if ($entry.Usable) {
                if ($text[1] -ne $entry.ModelClientPath) { throw 'A reused row lost its model path.' }
                if ($entry.DisplayId -eq 101 -and ($text[0] -notmatch 'scale 3$' -or $text[2] -notmatch 'FirstSkin.blp')) {
                    throw 'Appearance scale or texture details regressed.'
                }
                if ($entry.DisplayId -eq 103 -and $text[2] -notmatch 'none / embedded') { throw 'Embedded texture fallback regressed.' }
            } elseif ($text[1] -ne 'No model path' -or $text[2] -ne $entry.Finding) {
                throw 'Missing-model entries must remain visible with their diagnostics.'
            }

            # ContentPresenter calls the same recycling overload seen in the crash stack.
            $presenter.Content = $null
            $presenter.UpdateChild()
            $empty = $template.Build($null, $card)
            if ($null -ne $empty -and @([Avalonia.VisualTree.VisualExtensions]::GetVisualDescendants($empty) | Where-Object { $_ -is [Avalonia.Controls.TextBlock] -and $_.Text }).Count -gt 0) {
                throw 'Clearing a row left a stale appearance behind.'
            }
        }
    }
    Write-Output 'PASS creature appearance template: empty item, 300 content/clear cycles, rebind, measured cards, texture/scale details, and visible missing-model diagnostics.'
} finally {
    if ($null -ne $view) { $view.Dispose() }
    $session.Dispose()
}

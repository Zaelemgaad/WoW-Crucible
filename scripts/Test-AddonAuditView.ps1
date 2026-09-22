#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string]$DesktopDirectory, [Parameter(Mandatory)][string]$AddonDirectory)
$ErrorActionPreference = 'Stop'
if (!$IsWindows -or [Environment]::Version.Major -lt 10) { throw 'Requires Windows and PowerShell 7.6+ (.NET 10).' }
$directory = (Get-Item -LiteralPath $DesktopDirectory).FullName
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
foreach ($name in 'libSkiaSharp.dll','libHarfBuzzSharp.dll') {
    $path = Join-Path $directory $name
    if (!(Test-Path -LiteralPath $path)) { $path = Join-Path $directory "runtimes\win-$architecture\native\$name" }
    [void][Runtime.InteropServices.NativeLibrary]::Load($path)
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'Avalonia.Controls.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $directory 'Avalonia.Desktop.dll'))
[void][Avalonia.AppBuilderDesktopExtensions]::UsePlatformDetect([Avalonia.AppBuilder]::Configure[Avalonia.Application]()).SetupWithoutStarting()
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $directory 'WoWCrucible.Desktop.dll'))
$type = $assembly.GetType('WoWCrucible.Desktop.AddonAuditView', $true)
$view = [Activator]::CreateInstance($type, $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
function Field([string]$name) { return $type.GetField($name, $flags).GetValue($view) }
$report = [WoWCrucible.Core.AddonAuditService]::Scan((Get-Item -LiteralPath $AddonDirectory).FullName)
if (!$report.Packages.Count) { throw 'Test needs at least one addon package.' }
$type.GetField('_report', $flags).SetValue($view, $report)
$type.GetMethod('Filter', $flags).Invoke($view, $null)
$list = Field '_packages'
$template = $list.ItemTemplate
$empty = $template.Build($null)
if ($empty -isnot [Avalonia.Controls.Grid]) { throw 'Recycled null row must be empty.' }
$list.SelectedItem = $report.Packages[0]
if (!(Field '_details').Text.Contains($report.Packages[0].Directory)) { throw 'Selection did not populate the inspector.' }
(Field '_search').Text = 'no-such-addon-6494974'
[Avalonia.Threading.Dispatcher]::UIThread.RunJobs()
if (@($list.ItemsSource).Count -ne 0 -or (Field '_details').Text) { throw 'Filtering left stale rows or details.' }
(Field '_search').Text = ''
[Avalonia.Threading.Dispatcher]::UIThread.RunJobs()
for ($i=0; $i -lt 20; $i++) {
    $card = $template.Build($report.Packages[0])
    $card.Measure([Avalonia.Size]::new(500, [double]::PositiveInfinity))
    $empty = $template.Build($null, $card)
    if ($empty -isnot [Avalonia.Controls.Grid]) { throw 'Recycling a populated row retained its content.' }
}
$view.Measure([Avalonia.Size]::new(1000, 700))
$view.Arrange([Avalonia.Rect]::new(0,0,1000,700))
'Addon audit GUI passed: construction, selection, filtering, inspector clearing, null recycling and layout. No window or global input used.'

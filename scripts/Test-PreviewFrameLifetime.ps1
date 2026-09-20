#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$DesktopDirectory)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or [Environment]::Version.Major -lt 10) { throw 'Use PowerShell 7.6+ on Windows with .NET 10+.' }
$directory = (Get-Item -LiteralPath $DesktopDirectory).FullName
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
foreach ($library in @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
    $path = Join-Path $directory $library
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $path = Join-Path $directory "runtimes\win-$architecture\native\$library" }
    [void][Runtime.InteropServices.NativeLibrary]::Load($path)
}
$assemblies = @('Avalonia.Base', 'Avalonia.Controls', 'Avalonia.Desktop', 'Avalonia.Skia', 'SkiaSharp', 'WoWCrucible.Core', 'WoWCrucible.Desktop')
$references = @($assemblies | ForEach-Object {
    $path = Join-Path $directory "$_`.dll"
    [void][Reflection.Assembly]::LoadFrom($path)
    $path
}) + @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName)
[void][Avalonia.AppBuilderDesktopExtensions]::UsePlatformDetect([Avalonia.AppBuilder]::Configure[Avalonia.Application]()).SetupWithoutStarting()
Add-Type -Path (Join-Path $PSScriptRoot 'PreviewFrameLifetimeRegression.cs') -ReferencedAssemblies $references
[PreviewFrameLifetimeRegression]::Run()

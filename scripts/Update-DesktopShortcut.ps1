[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ShortcutPath,
    [string]$SettingsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$SettingsPath) { $SettingsPath = Join-Path $PSScriptRoot '..\.local\desktop-shortcut.json' }
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Windows shortcuts require Windows.'
}

$executable = Get-Item -LiteralPath $ExecutablePath
if ($executable.PSIsContainer -or $executable.Extension -ne '.exe' -or
    $executable.VersionInfo.ProductName -ne 'WoWCrucible.Desktop') {
    throw 'Select a successfully built WoWCrucible.Desktop.exe.'
}
$settingsFile = [IO.Path]::GetFullPath($SettingsPath)
$register = $PSBoundParameters.ContainsKey('ShortcutPath')
if (!$register) {
    $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    if ($settings.Version -ne 1) { throw 'Unsupported desktop-shortcut settings version.' }
    $ShortcutPath = $settings.ShortcutPath
}
if ([string]::IsNullOrWhiteSpace($ShortcutPath) -or [IO.Path]::GetExtension($ShortcutPath) -ne '.lnk') {
    throw 'Choose a .lnk shortcut path.'
}
$destination = [IO.Path]::GetFullPath($ShortcutPath)
$directory = [IO.Path]::GetDirectoryName($destination)
if (![IO.Directory]::Exists($directory)) { throw "Shortcut folder does not exist: $directory" }

# Publish a complete shortcut atomically, so a failed update leaves the last one intact.
$temporary = Join-Path $directory ('.crucible-' + [Guid]::NewGuid().ToString('N') + '.tmp.lnk')
$shell = $null
$link = $null
try {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($temporary)
    $link.TargetPath = $executable.FullName
    $link.WorkingDirectory = $executable.DirectoryName
    $link.IconLocation = $executable.FullName + ',0'
    $link.Description = 'WoW Crucible - latest successful local build (' + $executable.VersionInfo.ProductVersion + ')'
    $link.WindowStyle = 1
    $link.Save()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
    $link = $shell.CreateShortcut($temporary)
    if ($link.TargetPath -ne $executable.FullName -or $link.WorkingDirectory -ne $executable.DirectoryName) {
        throw 'The saved shortcut did not retain its target and working directory.'
    }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
    $link = $null
    if ([IO.File]::Exists($destination)) { [IO.File]::Replace($temporary, $destination, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $destination) }

    if ($register) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($settingsFile))
        $json = @{ Version = 1; ShortcutPath = $destination } | ConvertTo-Json
        [IO.File]::WriteAllText($settingsFile, $json, [Text.UTF8Encoding]::new($false))
    }
    Write-Output "Desktop shortcut updated: $destination -> $($executable.FullName)"
} finally {
    if ($null -ne $link) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}

[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ExecutablePath)

$ErrorActionPreference = 'Stop'
$update = Join-Path $PSScriptRoot 'Update-DesktopShortcut.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('crucible-shortcut-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$shortcut = Join-Path $root 'Crucible test.lnk'
$settings = Join-Path $root 'settings.json'
$executable = (Get-Item -LiteralPath $ExecutablePath).FullName
$shell = $null
$link = $null
try {
    & $update -ExecutablePath $executable -ShortcutPath $shortcut -SettingsPath $settings
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    if ($link.TargetPath -ne $executable -or $link.WorkingDirectory -ne [IO.Path]::GetDirectoryName($executable) -or $link.Arguments) {
        throw 'Shortcut target, working directory, or arguments are incorrect.'
    }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
    $link = $null
    if ((Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).ShortcutPath -ne $shortcut) {
        throw 'Shortcut registration was not retained.'
    }

    & $update -ExecutablePath $executable -SettingsPath $settings
    Remove-Item -LiteralPath $shortcut
    & $update -ExecutablePath $executable -SettingsPath $settings
    if (!(Test-Path -LiteralPath $shortcut)) { throw 'A subsequent build could not restore the registered shortcut.' }
    $before = (Get-FileHash -LiteralPath $shortcut -Algorithm SHA256).Hash
    $rejected = $false
    try { & $update -ExecutablePath (Join-Path $root 'missing.exe') -SettingsPath $settings }
    catch { $rejected = $true }
    if (!$rejected -or (Get-FileHash -LiteralPath $shortcut -Algorithm SHA256).Hash -ne $before) {
        throw 'A missing executable replaced the working shortcut.'
    }
    $rejected = $false
    try { & $update -ExecutablePath $settings -SettingsPath $settings }
    catch { $rejected = $true }
    if (!$rejected -or (Get-FileHash -LiteralPath $shortcut -Algorithm SHA256).Hash -ne $before) {
        throw 'A non-application file replaced the working shortcut.'
    }
    if (@(Get-ChildItem -LiteralPath $root -Filter '*.tmp.lnk' -Force).Count -ne 0) {
        throw 'Shortcut temporary files were left behind.'
    }
    Write-Output 'PASS desktop shortcut: registration, target, recreation, invalid builds, and temporary-file cleanup.'
} finally {
    if ($null -ne $link) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    # Only these exact fixture files are owned by this test; do not recursively delete a directory.
    foreach ($path in @($shortcut, $settings)) {
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($root, $false)
}

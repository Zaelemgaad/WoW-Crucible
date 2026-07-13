$ErrorActionPreference = 'Stop'
$cliDirectory = Join-Path $PSScriptRoot 'dist'
if (-not (Test-Path (Join-Path $cliDirectory 'wowcrucible.exe'))) {
    throw "wowcrucible.exe was not found in $cliDirectory"
}

$current = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($current -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($parts -notcontains $cliDirectory) {
    [Environment]::SetEnvironmentVariable('Path', (($parts + $cliDirectory) -join ';'), 'User')
    Write-Host "Added to your user PATH: $cliDirectory"
} else {
    Write-Host 'WoW Crucible CLI is already on your user PATH.'
}
Write-Host 'Open a new PowerShell or Command Prompt, then run: wowcrucible --help'

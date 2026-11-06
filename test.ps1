[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$SchemaPath = $env:WOW_CRUCIBLE_TEST_SCHEMA,

    [Parameter(Position = 1)]
    [string]$DbcDirectory = $env:WOW_CRUCIBLE_TEST_DBC,

    [string]$ScratchRoot = $env:WOW_CRUCIBLE_TEST_TEMP_ROOT
)

$ErrorActionPreference = 'Stop'
$repository = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScratchRoot)) {
    # StormLib still has legacy Windows path-length edges. Keep automated scratch
    # inside the repository workspace, but deliberately make the internal prefix tiny.
    $ScratchRoot = Join-Path $repository '.local\t'
}
$ScratchRoot = [IO.Path]::GetFullPath($ScratchRoot)
if ([IO.Path]::GetPathRoot($ScratchRoot).Equals('Z:\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Z: is the shared field-notes/network drive and is forbidden as a Crucible test scratch root. Use G: on this workstation.'
}
$runId = ([Guid]::NewGuid().ToString('N')).Substring(0, 8)
$runScratch = Join-Path $ScratchRoot ("r-{0}-{1}" -f $PID, $runId)

if ([string]::IsNullOrWhiteSpace($SchemaPath) -or [string]::IsNullOrWhiteSpace($DbcDirectory)) {
    Write-Host 'WoW Crucible corpus test runner'
    Write-Host ''
    Write-Host 'Usage:'
    Write-Host '  .\test.ps1 "<WotLK 3.3.5 (12340).xml>" "<full DBC directory>"'
    Write-Host ''
    Write-Host 'Or set WOW_CRUCIBLE_TEST_SCHEMA and WOW_CRUCIBLE_TEST_DBC.'
    exit 2
}

function Invoke-TestSuite([string]$Project, [string[]]$Arguments) {
    Write-Host ''
    Write-Host "=== $Project $Arguments"
    & dotnet run --project "tests\$Project\$Project.csproj" -c Release --no-build -- @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Project $Arguments (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

$SchemaPath = [IO.Path]::GetFullPath($SchemaPath)
$DbcDirectory = [IO.Path]::GetFullPath($DbcDirectory)

Push-Location $repository
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    New-Item -ItemType Directory -Path $runScratch -Force | Out-Null
    Write-Host "Test scratch: $runScratch"
    # Before the scratch TEMP: build servers outlive this script and would keep the scratch folder in use.
    & dotnet build WoWCrucible.slnx -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $env:TEMP = $runScratch
    $env:TMP = $runScratch

    Invoke-TestSuite WoWCrucible.Core.Tests @($SchemaPath, $DbcDirectory)
    # Without their optional client, corpus or 7-Zip inputs, these run on built-in fixtures.
    foreach ($suite in '--item-tooltip', '--model-browser', '--tooltip-audit', '--class-studio') {
        Invoke-TestSuite WoWCrucible.Core.Tests @($suite)
    }

    # Headless, never-shown windows with isolated settings. Keep the flags that reach a live server or
    # activate every control out of this list: --item-browser-live, --item-tooltip-live, --coa, --activate.
    foreach ($suite in @(), @('--mpq-extraction'), @('--guided-editors'), @('--workspace-shell'), @('--ui-coverage'),
        @('--embedded-studio'), @('--ported-workflows'), @('--ui-stress', '180026', '2000')) {
        Invoke-TestSuite WoWCrucible.Desktop.Tests $suite
    }
    exit 0
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    if (Test-Path -LiteralPath $runScratch) {
        Remove-Item -LiteralPath $runScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
}

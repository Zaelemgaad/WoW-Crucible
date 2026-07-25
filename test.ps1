[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$SchemaPath = $env:WOW_CRUCIBLE_TEST_SCHEMA,

    [Parameter(Position = 1)]
    [string]$DbcDirectory = $env:WOW_CRUCIBLE_TEST_DBC
)

$ErrorActionPreference = 'Stop'
$repository = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($SchemaPath) -or [string]::IsNullOrWhiteSpace($DbcDirectory)) {
    Write-Host 'WoW Crucible corpus test runner'
    Write-Host ''
    Write-Host 'Usage:'
    Write-Host '  .\test.ps1 "<WotLK 3.3.5 (12340).xml>" "<full DBC directory>"'
    Write-Host ''
    Write-Host 'Or set WOW_CRUCIBLE_TEST_SCHEMA and WOW_CRUCIBLE_TEST_DBC.'
    exit 2
}

$SchemaPath = [IO.Path]::GetFullPath($SchemaPath)
$DbcDirectory = [IO.Path]::GetFullPath($DbcDirectory)

Push-Location $repository
try {
    & dotnet build WoWCrucible.slnx -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & dotnet run --project tests\WoWCrucible.Core.Tests\WoWCrucible.Core.Tests.csproj -c Release --no-build -- $SchemaPath $DbcDirectory
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}

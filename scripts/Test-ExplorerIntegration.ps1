[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [switch]$KeepInstalled,
    [string]$DbcDirectory
)

$ErrorActionPreference = 'Stop'
$executable = (Get-Item -LiteralPath $ExecutablePath).FullName
$root = Join-Path ([IO.Path]::GetTempPath()) ('Crucible-Explorer-' + [Guid]::NewGuid().ToString('N'))
$report = Join-Path ([IO.Path]::GetDirectoryName($executable)) 'Logs\Explorer\last-export.json'
$ownedFiles = [Collections.Generic.List[string]]::new()
$ownedDirectories = [Collections.Generic.List[string]]::new()
$oldRegistration = 'HKCU:\Software\Classes\CLSID\{f89ebc30-9206-4c4b-b222-db59b290a935}\LocalServer32'
$oldCommand = if (Test-Path -LiteralPath $oldRegistration) { (Get-Item -LiteralPath $oldRegistration).GetValue('') } else { $null }

function Invoke-Registration([string]$Mode) {
    $process = Start-Process -FilePath $executable -ArgumentList @($Mode, '--quiet') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Registration failed ($($process.ExitCode)); see $report" }
}

function New-Fixture([string]$Directory, [string]$Name = 'SpellDuration.dbc') {
    [void][IO.Directory]::CreateDirectory($Directory)
    if (!$ownedDirectories.Contains($Directory)) { $ownedDirectories.Add($Directory) }
    $path = Join-Path $Directory $Name
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($path))
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WDBC'))
        $writer.Write([int]1); $writer.Write([int]4); $writer.Write([int]16); $writer.Write([int]1)
        $writer.Write([int]7); $writer.Write([int]1234); $writer.Write([int]0); $writer.Write([int]6000); $writer.Write([byte]0)
    } finally { $writer.Dispose() }
    $ownedFiles.Add($path)
    foreach ($extension in @('.csv', '.json')) { $ownedFiles.Add([IO.Path]::ChangeExtension($path, $extension)) }
    return $path
}

function Invoke-Batch([string]$ClassId, [string[]]$Paths, [string]$Format, [bool]$ThroughMenu = $false) {
    $previous = if (Test-Path -LiteralPath $report) { [IO.File]::ReadAllText($report) } else { '' }
    if ($ThroughMenu) { [CrucibleExplorerSmoke]::InvokeMenu($Paths, 'Convert to CSV') }
    else { [CrucibleExplorerSmoke]::Invoke($ClassId, $Paths) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 90) {
        if (Test-Path -LiteralPath $report) {
            $content = [IO.File]::ReadAllText($report)
            if ($content -ne $previous) {
                $result = $content | ConvertFrom-Json
                if ($result.Error) { throw $result.Error }
                if ($result.Format -eq $Format) { return $result.Result }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Explorer export timed out.'
}

Add-Type -Path (Join-Path $PSScriptRoot 'fixtures\ExplorerShellSmoke.cs')
try {
    Invoke-Registration '--install-explorer-menu'
    foreach ($extension in @('.dbc', '.db2')) {
        $menu = Get-Item -LiteralPath "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\WoWCrucible"
        if ($menu.GetValue('ExtendedSubCommandsKey') -ne 'WoWCrucible.Explorer') { throw 'Cascading menu registration is incorrect.' }
    }
    $paths = @(0..511 | ForEach-Object { New-Fixture (Join-Path $root ('Folder ' + $_)) })
    if (($paths -join ' ').Length -lt 32767) { throw 'The bulk fixture must exceed the Windows command-line length limit.' }
    $before = @{}
    foreach ($path in $paths) { $before[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    $csvId = '{F89EBC30-9206-4C4B-B222-DB59B290A935}'
    $jsonId = '{DA5858C0-88A2-48F6-B251-935928864C0F}'
    $result = Invoke-Batch $csvId $paths 'Csv'
    if ($result.Exported -ne 512 -or $result.Failed -ne 0) { throw "512-file CSV selection failed: $($result | ConvertTo-Json -Depth 4)" }
    foreach ($path in $paths) {
        $rows = @(Import-Csv -LiteralPath ([IO.Path]::ChangeExtension($path, '.csv')))
        if ($rows.Count -ne 1 -or $rows[0].Duration -ne '1234') { throw "CSV values differ: $path" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $before[$path]) { throw 'An input was changed.' }
    }
    $result = Invoke-Batch $jsonId $paths[0..2] 'Json'
    if ($result.Exported -ne 3) { throw 'Multi-file JSON selection failed.' }
    $json = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($paths[0], '.json')) -Raw | ConvertFrom-Json
    if ($json[0].Duration -ne 1234) { throw 'JSON values differ.' }
    $result = Invoke-Batch $csvId $paths[0..1] 'Csv'
    if ($result.Skipped -ne 2 -or $result.Exported -ne 0) { throw 'Existing output was not skipped.' }

    $menuDirectory = Join-Path $root 'Mixed selection'
    $dbc = New-Fixture $menuDirectory 'SpellDuration.dbc'
    $db2 = New-Fixture $menuDirectory 'SpellDuration.db2'
    $entries = [CrucibleExplorerSmoke]::Menu(@($dbc, $db2))
    foreach ($label in @('Crucible', 'Open', 'Convert to CSV', 'Convert to JSON')) {
        if ($entries -notcontains $label) { throw "The actual Explorer menu lacks '$label': $($entries -join ', ')" }
    }
    $result = Invoke-Batch $csvId @($dbc, $db2) 'Csv'
    if ($result.Failed -ne 2) { throw 'DBC/DB2 output collision was not rejected.' }
    $other = New-Fixture $menuDirectory 'SpellCastTimes.dbc'
    $result = Invoke-Batch $csvId @($dbc, $other) 'Csv' $true
    if ($result.Exported -ne 2) { throw 'Invoking the actual Explorer menu did not export the whole selection.' }
    if ($DbcDirectory) {
        $copies = @()
        foreach ($name in @('ScalingStatDistribution.dbc', 'ScalingStatValues.dbc')) {
            $source = Join-Path $DbcDirectory $name
            $copy = Join-Path $menuDirectory $name
            Copy-Item -LiteralPath $source -Destination $copy
            $ownedFiles.Add($copy)
            $ownedFiles.Add([IO.Path]::ChangeExtension($copy, '.csv'))
            $copies += $copy
        }
        $result = Invoke-Batch $csvId $copies 'Csv' $true
        if ($result.Exported -ne 2 -or $result.Items[0].Rows -lt 1) { throw 'Real scaling-stat DBC exports failed.' }
        Write-Output 'PASS Explorer: real ScalingStatDistribution and ScalingStatValues DBC copies exported through the menu.'
    }
    Write-Output 'PASS Explorer: cascading menu invocation, cold COM activation, 512-file CSV batch exceeding command-line limits, JSON batch, decoded values, untouched sources, collision and overwrite protection.'

    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $servers = @(Get-CimInstance Win32_Process -Filter "Name='WoWCrucible.Desktop.exe'" | Where-Object { $_.ExecutablePath -eq $executable -and $_.CommandLine -like '*--explorer-server*' })
        if ($servers.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($servers.Count -ne 0) { throw 'An idle Explorer export server did not exit.' }
    Write-Output 'PASS Explorer: export worker exits without leaving an editor or background service running.'
    Invoke-Registration '--remove-explorer-menu'
    if (Test-Path -LiteralPath 'HKCU:\Software\Classes\SystemFileAssociations\.dbc\shell\WoWCrucible') { throw 'Removing the Explorer menu left its DBC registration.' }
    if (Test-Path -LiteralPath $oldRegistration) { throw 'Removing the Explorer menu left its COM registration.' }
    Invoke-Registration '--install-explorer-menu'
    Write-Output 'PASS Explorer: menu can be removed and installed again without administrator rights.'
} finally {
    if (!$KeepInstalled) {
        Invoke-Registration '--remove-explorer-menu'
        if ($oldCommand -match '^"([^"]+)" --explorer-server$') {
            $restore = Start-Process -FilePath $Matches[1] -ArgumentList @('--install-explorer-menu', '--quiet') -WindowStyle Hidden -Wait -PassThru
            if ($restore.ExitCode -ne 0) { throw 'Could not restore the previous Explorer registration.' }
        }
    }
    foreach ($path in $ownedFiles) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
    foreach ($directory in $ownedDirectories) { if ([IO.Directory]::Exists($directory)) { [IO.Directory]::Delete($directory, $false) } }
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $false) }
}

[CmdletBinding()]
param(
    [string]$StageDirectory,
    [string]$ManifestPath,
    [string]$MSBuildPath,
    [string]$OutputDirectory,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$releaseRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$artifactsRoot = Join-Path $releaseRoot 'artifacts'
if (-not $StageDirectory) { $StageDirectory = Join-Path $artifactsRoot 'stage' }
if (-not $ManifestPath) { $ManifestPath = Join-Path $releaseRoot 'release-payload-manifest.json' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifactsRoot 'packaged-source-checks' }
$StageDirectory = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\')
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')

function IsWithin([string]$Path, [string]$Directory) {
    return $Path.StartsWith(($Directory.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)
}
function RejectReparsePath([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            if ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw ('Reparse points are not allowed: ' + $candidate)
            }
        }
        $parent = Split-Path -Parent $candidate
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
}
function RelativeName([string]$Name) {
    if ([String]::IsNullOrWhiteSpace($Name) -or [IO.Path]::IsPathRooted($Name) -or $Name.Contains(':')) {
        throw 'Payload entries must use relative paths.'
    }
    $parts = $Name -split '[\\/]'
    if (@($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count) {
        throw 'Payload entries must not contain empty or traversal segments.'
    }
    return $parts -join '\'
}
function StagePath([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $StageDirectory (RelativeName $Relative)))
    if (-not (IsWithin $path $StageDirectory)) { throw 'Payload path escaped staging.' }
    return $path
}

# All generated files stay under this checkout's ignored artifacts directory.
if ($OutputDirectory -ne $artifactsRoot -and -not (IsWithin $OutputDirectory $artifactsRoot)) {
    throw 'Use an output directory within this checkout''s artifacts directory.'
}
if ($OutputDirectory -eq $StageDirectory -or (IsWithin $OutputDirectory $StageDirectory)) {
    throw 'Rebuild output must not be written into staging.'
}
RejectReparsePath $OutputDirectory
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$runDirectory = Join-Path $OutputDirectory ('ps-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runDirectory
if (-not $ReportPath) { $ReportPath = Join-Path $runDirectory 'report.json' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
if (-not (IsWithin $ReportPath $artifactsRoot) -or [IO.Path]::GetExtension($ReportPath) -ne '.json' -or
    $ReportPath -eq $StageDirectory -or (IsWithin $ReportPath $StageDirectory)) {
    throw 'Use a JSON report path under artifacts and outside staging.'
}
RejectReparsePath $ReportPath
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force

$watch = [Diagnostics.Stopwatch]::StartNew()
$version = $null
$manifestFiles = 0
$payloadVerified = $false
$results = New-Object Collections.Generic.List[object]
$failure = $null
try {
    $allowedStages = @((Join-Path $releaseRoot 'stage'), (Join-Path $artifactsRoot 'stage'))
    if (-not @($allowedStages | Where-Object { [String]::Equals($_, $StageDirectory, [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw 'Use this checkout''s stage or artifacts/stage directory, never a live installation.'
    }
    RejectReparsePath $StageDirectory
    RejectReparsePath $ManifestPath
    if (-not (Test-Path -LiteralPath $StageDirectory -PathType Container)) { throw 'Staged payload was not found.' }
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw 'The release payload manifest was not found.' }
    $definitionPath = Join-Path $PSScriptRoot 'release-files.json'
    RejectReparsePath $definitionPath
    $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $version = $definition.Version
    $expected = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in (@($definition.FixedPayloadFiles) + @($definition.DependencyFiles) + @($definition.SourceFiles | ForEach-Object { 'source/' + $_ }))) {
        if (-not $expected.Add((RelativeName $name))) { throw 'The release definition contains duplicate payload paths.' }
    }
    $manifest = @(Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    $indexed = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest) {
        $name = RelativeName $entry.Path
        if (-not $expected.Contains($name) -or $indexed.ContainsKey($name) -or $entry.SHA256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $null -eq $entry.Bytes -or [long]$entry.Bytes -lt 0) { throw 'The payload manifest is invalid or differs from the release definition.' }
        $indexed.Add($name, $entry)
    }
    if ($indexed.Count -ne $expected.Count) { throw 'The payload manifest does not cover the complete release definition.' }
    $manifestFiles = $indexed.Count
    function Verify-Payload {
        $actual = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in Get-ChildItem -LiteralPath $StageDirectory -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staged payload contains a reparse point.' }
            if ($item.PSIsContainer) { continue }
            $name = $item.FullName.Substring($StageDirectory.Length + 1)
            if (-not $expected.Contains($name) -or -not $actual.Add($name)) { throw ('Unexpected staged file: ' + $name) }
            $entry = $indexed[$name]
            if ($item.Length -ne [long]$entry.Bytes -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ne $entry.SHA256) {
                throw ('Staged payload hash or size differs from its manifest: ' + $name)
            }
        }
        if ($actual.Count -ne $expected.Count) { throw 'A required staged file is missing.' }
    }
    Verify-Payload
    $payloadVerified = $true
    $desktopVersion = (Get-Item -LiteralPath (StagePath 'WirelessInternet.exe')).VersionInfo.FileVersion
    if ($desktopVersion -ne ($version + '.0')) { throw 'The staged executable version differs from the release definition.' }

    $source = Join-Path $runDirectory 'source'
    $null = New-Item -ItemType Directory -Path $source
    foreach ($name in $definition.SourceFiles) {
        $relative = RelativeName $name
        $to = [IO.Path]::GetFullPath((Join-Path $source $relative))
        if (-not (IsWithin $to $source)) { throw 'A source copy escaped the isolated rebuild directory.' }
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force
        Copy-Item -LiteralPath (StagePath ('source/' + $relative)) -Destination $to
    }
    # The projects read this resource relative to source/, just as in an installed package.
    $support = Join-Path $runDirectory 'support'
    $null = New-Item -ItemType Directory -Path $support
    Copy-Item -LiteralPath (StagePath 'support/wireless.ico') -Destination (Join-Path $support 'wireless.ico')
    if (-not $MSBuildPath) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $found = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
                Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
            if ($found.Count) { $MSBuildPath = $found[0] }
        }
        if (-not $MSBuildPath) { $MSBuildPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe' }
    }
    $MSBuildPath = [IO.Path]::GetFullPath($MSBuildPath)
    if (-not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) { throw 'An existing MSBuild is required; this check never downloads tools.' }
    $arguments = @((Join-Path $source 'WirelessInternet.sln'), '/nologo', '/m:1', '/nr:false', '/t:Rebuild', '/v:minimal',
        '/p:Configuration=Release', '/p:Platform=x64', '/p:UseSharedCompilation=false')
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $buildLines = @(& $MSBuildPath @arguments 2>&1 | ForEach-Object { $_.ToString() })
        $buildExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    [IO.File]::WriteAllLines((Join-Path $runDirectory 'build.log'), [string[]]$buildLines, [Text.UTF8Encoding]::new($false))
    foreach ($line in $buildLines) { Write-Host $line }
    if ($buildExitCode -ne 0) { throw ('Packaged source rebuild failed with exit code ' + $buildExitCode + '.') }
    $binaries = @(
        @{ Name = 'WirelessInternet.exe'; Staged = 'WirelessInternet.exe' },
        @{ Name = 'ConnectionWorker.exe'; Staged = 'support/ConnectionWorker.exe' },
        @{ Name = 'RelayLogHost.exe'; Staged = 'support/RelayLogHost.exe' }
    )
    foreach ($binary in $binaries) {
        foreach ($suffix in @('', '.config')) {
            $name = $binary.Name + $suffix
            $rebuilt = Join-Path (Join-Path $source 'bin') $name
            if (-not (Test-Path -LiteralPath $rebuilt -PathType Leaf)) { throw ('Rebuild did not create ' + $name) }
            $stagedHash = (Get-FileHash -LiteralPath (StagePath ($binary.Staged + $suffix)) -Algorithm SHA256).Hash
            $rebuiltHash = (Get-FileHash -LiteralPath $rebuilt -Algorithm SHA256).Hash
            $results.Add([pscustomobject]@{ Path = $binary.Staged + $suffix; Matches = $stagedHash -eq $rebuiltHash;
                StagedSHA256 = $stagedHash; RebuiltSHA256 = $rebuiltHash })
        }
    }
    if (@($results | Where-Object { -not $_.Matches }).Count) { throw 'Packaged source did not reproduce every executable and runtime configuration.' }
    Verify-Payload
}
catch { $failure = $_ }
$watch.Stop()
$report = [pscustomobject]@{
    Passed = $null -eq $failure
    Version = $version
    StageDirectory = $StageDirectory
    ManifestFiles = $manifestFiles
    PayloadVerified = $payloadVerified
    ComparedFiles = $results.Count
    Files = @($results.ToArray())
    MSBuildPath = $MSBuildPath
    RunDirectory = $runDirectory
    ReportPath = $ReportPath
    DurationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    Error = $(if ($failure) { $failure.Exception.Message } else { $null })
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
if ($failure) { throw $failure }
Write-Host ('PASS packaged source: ' + $results.Count + ' files reproduced (' + $report.DurationSeconds + 's).')
$report

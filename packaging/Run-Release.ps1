[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DependencyDirectory,
    [Parameter(Mandatory=$true)][string]$InnoCompilerPath,
    [string]$SourceDirectory,
    [string]$DocumentationDirectory,
    [string]$MSBuildPath,
    [string]$ChineseMessagesFile,
    [switch]$FullRegression
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$releaseRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$artifacts = Join-Path $releaseRoot 'artifacts'
$reports = Join-Path $artifacts 'reports'
$stage = Join-Path $artifacts 'stage'
$output = Join-Path $artifacts 'release'
$reportPath = Join-Path $reports 'release-validation.json'
$packageReport = Join-Path $reports 'packaged-source.json'

function Reject-Reparse([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if ((Test-Path -LiteralPath $candidate) -and
            ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Release paths must not contain reparse points: ' + $candidate)
        }
        $parent = Split-Path -Parent $candidate
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
}
Reject-Reparse $artifacts
$null = New-Item -ItemType Directory -Path $artifacts -Force
# Developer-only serialization, independent of the sharing application's locks.
$lease = [IO.File]::Open((Join-Path $artifacts 'release.lock'), [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$watch = [Diagnostics.Stopwatch]::StartNew()
$steps = New-Object Collections.Generic.List[object]
$report = [ordered]@{
    SchemaVersion = 1; RunId = [Guid]::NewGuid().ToString('N'); Passed = $false; Version = $null
    TestMode = $(if ($FullRegression) { 'QuickAndFull' } else { 'Quick' })
    StartedAtUtc = [DateTime]::UtcNow.ToString('o'); DurationSeconds = 0
    SourceRevision = $env:GITHUB_SHA; Steps = $steps; FailedStage = $null; Error = $null
    InstallerPath = $null; InstallerSHA256 = $null; ChecksumPath = $null
    PackagedSourceReport = $packageReport; MSBuildPath = $null; InnoCompilerPath = $null
    InnoCompilerVersion = $null; ChineseMessagesFile = $null; ReportPath = $reportPath
    ManualAcceptance = @('Android connectivity and protection', 'GUI and DPI', 'Installer execution when relevant')
}
$currentStage = 'Preflight'
$reportReady = $false
function Write-Report {
    if (-not $reportReady) { return }
    Reject-Reparse $reportPath
    $report.DurationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
function Invoke-ReleaseStep([string]$Name, [string]$LogName, [scriptblock]$Body) {
    $stepWatch = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $reports $LogName
    Reject-Reparse $log
    $writer = [IO.StreamWriter]::new($log, $false, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $passed = $false
    Write-Host ('== ' + $Name + ' ==')
    try {
        & $Body *>&1 | ForEach-Object { $writer.WriteLine($_.ToString()); Write-Host $_ }
        $passed = $true
    }
    catch { $writer.WriteLine($_.Exception.Message); throw }
    finally {
        $writer.Dispose()
        $steps.Add([pscustomobject]@{ Name = $Name; Passed = $passed;
            DurationSeconds = [Math]::Round($stepWatch.Elapsed.TotalSeconds, 3); LogPath = $log })
        Write-Report
    }
}
try {
    Reject-Reparse $reports
    Reject-Reparse $stage
    Reject-Reparse $output
    $null = New-Item -ItemType Directory -Path $reports -Force
    Reject-Reparse $reportPath
    $reportReady = $true
    # A failed repeat must not leave an earlier successful subreport or old logs.
    Reject-Reparse $packageReport
    [pscustomobject]@{ Passed = $false; State = 'NotRun'; RunId = $report.RunId } |
        ConvertTo-Json | Set-Content -LiteralPath $packageReport -Encoding UTF8
    foreach ($name in @('tests.log', 'full-tests.log', 'build.log', 'rebuild.log')) {
        $logPath = Join-Path $reports $name
        Reject-Reparse $logPath
        [IO.File]::WriteAllText($logPath, ('Not run for release ' + $report.RunId + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    }
    if (-not $SourceDirectory) {
        $SourceDirectory = Join-Path $releaseRoot 'source'
        if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) { $SourceDirectory = Join-Path $releaseRoot 'gui' }
    }
    if (-not $DocumentationDirectory) { $DocumentationDirectory = $releaseRoot }
    $SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
    $DocumentationDirectory = [IO.Path]::GetFullPath($DocumentationDirectory)
    $DependencyDirectory = [IO.Path]::GetFullPath($DependencyDirectory)
    $InnoCompilerPath = [IO.Path]::GetFullPath($InnoCompilerPath)
    foreach ($path in @($SourceDirectory, $DocumentationDirectory, $DependencyDirectory, $InnoCompilerPath)) { Reject-Reparse $path }
    if (-not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) { throw 'Pass an existing Inno Setup compiler; this entry point never installs tools.' }
    if (Test-Path -LiteralPath (Join-Path $DependencyDirectory 'state\wireless-session.json')) { throw 'Use clean dependency inputs, not a live sharing installation.' }
    $dependencyConfig = Join-Path $DependencyDirectory 'wireless-config.json'
    if (Test-Path -LiteralPath $dependencyConfig) {
        $configuration = Get-Content -LiteralPath $dependencyConfig -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $configuration -or $configuration.PhoneIdentity -or $configuration.ConnectEndpoint) { throw 'Dependency inputs contain device binding data; use a clean checkout or verified dependencies.' }
    }
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
    if (-not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) { throw 'An existing MSBuild is required.' }
    if (-not $ChineseMessagesFile) {
        foreach ($candidate in @((Join-Path $releaseRoot 'installer\Languages\ChineseSimplified.isl'),
            (Join-Path (Split-Path -Parent $InnoCompilerPath) 'Languages\ChineseSimplified.isl'))) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $ChineseMessagesFile = $candidate; break }
        }
    }
    if ($ChineseMessagesFile) {
        $ChineseMessagesFile = [IO.Path]::GetFullPath($ChineseMessagesFile)
        Reject-Reparse $ChineseMessagesFile
        if (-not (Test-Path -LiteralPath $ChineseMessagesFile -PathType Leaf)) { throw 'The selected installer language file was not found.' }
    }
    $definition = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($definition.Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
    $report.Version = $definition.Version
    $report.MSBuildPath = $MSBuildPath; $report.InnoCompilerPath = $InnoCompilerPath
    $report.InnoCompilerVersion = (Get-Item -LiteralPath $InnoCompilerPath).VersionInfo.FileVersion
    $report.ChineseMessagesFile = $ChineseMessagesFile
    Write-Report
    $currentStage = 'Quick tests'
    Invoke-ReleaseStep $currentStage 'tests.log' {
        & (Join-Path $releaseRoot 'tests\run-tests.ps1') -SourceDirectory $SourceDirectory
    }
    if ($FullRegression) {
        $currentStage = 'Historical regression'
        Invoke-ReleaseStep $currentStage 'full-tests.log' {
            & (Join-Path $releaseRoot 'tests\run-full.ps1') -SourceDirectory $SourceDirectory
        }
    }
    $currentStage = 'Build and installer'
    Invoke-ReleaseStep $currentStage 'build.log' {
        & (Join-Path $PSScriptRoot 'Build-Release.ps1') -SourceDirectory $SourceDirectory -DependencyDirectory $DependencyDirectory `
            -DocumentationDirectory $DocumentationDirectory -StageDirectory $stage -OutputDirectory $output `
            -MSBuildPath $MSBuildPath -InnoCompilerPath $InnoCompilerPath -ChineseMessagesFile $ChineseMessagesFile -CompileInstaller
    }
    $currentStage = 'Packaged source rebuild'
    Invoke-ReleaseStep $currentStage 'rebuild.log' {
        & (Join-Path $PSScriptRoot 'Test-PackagedSource.ps1') -StageDirectory $stage -MSBuildPath $MSBuildPath -ReportPath $packageReport
    }
    $currentStage = 'Verify artifacts'
    $installer = Join-Path $output ('WirelessInternet-' + $definition.Version + '-Setup-x64.exe')
    $checksum = $installer + '.sha256'
    $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $expectedChecksum = $actualHash + '  ' + [IO.Path]::GetFileName($installer)
    if ([IO.File]::ReadAllText($checksum).Trim() -ne $expectedChecksum) { throw 'Installer checksum file did not match the built artifact.' }
    $rebuild = Get-Content -LiteralPath $packageReport -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $rebuild.Passed -or $rebuild.ComparedFiles -ne 6) { throw 'Packaged-source acceptance did not complete.' }
    $report.InstallerPath = $installer; $report.InstallerSHA256 = $actualHash; $report.ChecksumPath = $checksum
    $report.Passed = $true
}
catch { $report.FailedStage = $currentStage; $report.Error = $_.Exception.Message; throw }
finally {
    try { Write-Report }
    finally { $lease.Dispose() }
}
Write-Host ('PASS release ' + $report.Version + ' (' + $report.DurationSeconds + 's); ' + $reportPath)
[pscustomobject]$report

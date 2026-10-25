param(
    [string]$SourceDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'source')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($env:WINDIR) -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'These offline checks require Windows x64 with .NET Framework 4.8.'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The .NET Framework x64 C# compiler was not found. No tools will be downloaded.'
}
$source = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceDirectory))
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw 'Source directory was not found. Use -SourceDirectory to select the repository source folder.'
}
$testRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$requiredSources = @('NativeNetwork.cs', 'RuntimeCommon.cs', 'WatchdogController.cs', 'PhoneGuardSource.cs',
    'ProcessExecution.cs', 'SetupBackend.cs', 'Backend.cs', 'MemoryLog.cs', 'SessionStatus.cs', 'UiOperationCoordinator.cs',
    'RuntimeSessionStore.cs', 'OwnedProcessManager.cs', 'AndroidProtocol.cs', 'AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs',
    'MonitoringModels.cs', 'SystemSessionObservationSource.cs', 'PhoneTrafficTracker.cs', 'MonitorSnapshotAssembler.cs')
foreach ($name in $requiredSources) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) {
        throw ('A required source file is missing: ' + $name)
    }
}
$runRoot = Join-Path (Join-Path $testRoot 'bin') ('offline-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot -Force
$summary = New-Object Collections.Generic.List[object]

function Invoke-CheckedNative([string]$Executable, [string[]]$Arguments, [string]$Label) {
    $previousPreference = $ErrorActionPreference
    try {
        # Capture native stderr too, then make the exit code authoritative on Windows PowerShell 5.1.
        $ErrorActionPreference = 'Continue'
        $lines = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $nativeExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($nativeExitCode -ne 0) {
        foreach ($line in $lines) { Write-Host $line }
        throw ($Label + ' failed with exit code ' + $nativeExitCode + '.')
    }
    return $lines
}

function Compile-Check([string]$Name, [string[]]$Sources, [string[]]$References, [string]$TestFile, [string[]]$ExtraTests = @(), [string]$EntryPoint = '') {
    $executable = Join-Path $runRoot ($Name + '.exe')
    $arguments = @('/nologo', '/langversion:5', '/platform:x64', '/target:exe', '/optimize+', '/utf8output', '/codepage:65001', ('/out:' + $executable))
    foreach ($reference in $References) { $arguments += ('/r:' + $reference) }
    if ($EntryPoint) { $arguments += ('/main:' + $EntryPoint) }
    foreach ($name in $Sources) { $arguments += (Join-Path $source $name) }
    $arguments += (Join-Path $testRoot $TestFile)
    foreach ($name in $ExtraTests) { $arguments += (Join-Path $testRoot $name) }
    $messages = @(Invoke-CheckedNative $compiler $arguments ('Compile ' + $Name))
    foreach ($message in $messages) { Write-Host $message }
    return $executable
}

function Run-Check([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$Pattern, [int]$Expected) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lines = @(Invoke-CheckedNative $Executable $Arguments $Name)
    $text = $lines -join [Environment]::NewLine
    if ($Name -eq 'Watchdog') {
        $report = $lines | Select-Object -Last 1 | ConvertFrom-Json
        if ($report.failures -ne 0 -or $report.realAdbCalls -ne 0 -or $report.assertions -ne $Expected) {
            throw 'Watchdog did not report the expected passing offline assertions.'
        }
        $actual = [int]$report.assertions
    }
    else {
        $matched = [regex]::Match($text, $Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
        if (-not $matched.Success -or [int]$matched.Groups[1].Value -ne $Expected) {
            foreach ($line in $lines) { Write-Host $line }
            throw ($Name + ' did not report its expected passing assertion count.')
        }
        $actual = [int]$matched.Groups[1].Value
    }
    $summary.Add([pscustomobject]@{ Suite = $Name; Assertions = $actual; Passed = $true })
    Write-Host ('PASS ' + $Name + ': ' + $actual + ' assertions (' + [Math]::Round($watch.Elapsed.TotalSeconds, 2) + 's)')
}

$coreReferences = @('System.Core.dll', 'System.Management.dll', 'System.Web.Extensions.dll')
$coreSources = @('RuntimeCommon.cs', 'NativeNetwork.cs', 'PhoneGuardSource.cs', 'ProcessExecution.cs', 'RuntimeSessionStore.cs', 'OwnedProcessManager.cs', 'AndroidProtocol.cs')
$monitorSources = @('Backend.cs', 'MonitoringModels.cs', 'SystemSessionObservationSource.cs', 'PhoneTrafficTracker.cs', 'MonitorSnapshotAssembler.cs', 'MemoryLog.cs', 'ProcessExecution.cs', 'SessionStatus.cs')
Push-Location $runRoot
try {
    $network = Compile-Check 'NativeNetworkTunRegression' @('NativeNetwork.cs') @('System.Core.dll', 'System.Management.dll') 'NativeNetworkTunRegression.cs'
    Run-Check 'Network' $network @() '^PASS (\d+) offline native-network' 69

    $watchdog = Compile-Check 'WatchdogChecks' ($coreSources + @('WatchdogController.cs')) $coreReferences 'WatchdogChecks.cs'
    Run-Check 'Watchdog' $watchdog @() '' 181

    $fixture = Compile-Check 'ProcessExecutionFixture' @() @('System.Core.dll') 'ProcessExecutionFixture.cs'
    $process = Compile-Check 'ProcessExecutionChecks' ($coreSources + @('SetupBackend.cs')) $coreReferences 'ProcessExecutionChecks.cs'
    Run-Check 'Process' $process @($fixture) '^PASS (\d+) shared-execution checks' 54

    $backend = Compile-Check 'BackendStatusChecks' $monitorSources @('System.Core.dll', 'System.Web.Extensions.dll') 'BackendStatusChecks.cs'
    Run-Check 'Backend' $backend @() '^TOTAL (\d+) PASS' 29

    $sampling = Compile-Check 'PhoneSamplingChecks' $monitorSources @('System.Core.dll', 'System.Web.Extensions.dll') 'PhoneSamplingChecks.cs'
    Run-Check 'Sampling' $sampling @($runRoot) '^PASS (\d+) sampling checks' 26

    $composition = Compile-Check 'MonitoringCompositionChecks' $monitorSources @('System.Core.dll', 'System.Web.Extensions.dll') 'MonitoringCompositionChecks.cs'
    Run-Check 'MonitoringComposition' $composition @((Join-Path $runRoot 'monitoring-composition-results.json')) '^PASS (\d+) monitoring composition checks' 272

    $coordinator = Compile-Check 'CoordinatorChecks' @('UiOperationCoordinator.cs') @('System.Core.dll', 'System.Web.Extensions.dll') 'CoordinatorChecks.cs'
    Run-Check 'Coordinator' $coordinator @((Join-Path $runRoot 'coordinator-results.json')) '^PASS (\d+) coordinator checks' 39

    $runtime = Compile-Check 'RuntimeServiceChecks' $coreSources $coreReferences 'RuntimeServiceChecks.cs'
    Run-Check 'RuntimeServices' $runtime @((Join-Path $runRoot 'runtime-services')) '^PASS (\d+) runtime service checks' 51

    $android = Compile-Check 'AndroidClientChecks' ($coreSources + @('AndroidDeviceClient.cs', 'SetupBackend.cs')) $coreReferences 'AndroidClientChecks.cs'
    Run-Check 'Android' $android @() '^PASS (\d+) Android client/protocol checks' 140

    $lifecycle = Compile-Check 'ConnectionLifecycleChecks' ($coreSources + @('AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs')) $coreReferences 'ConnectionLifecycleChecks.cs'
    Run-Check 'Lifecycle' $lifecycle @((Join-Path $runRoot 'lifecycle-results.json')) '^PASS (\d+) connection lifecycle assertions' 264

    $resultChecks = Compile-Check 'ConnectionResultChecks' ($coreSources + @('AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs')) $coreReferences 'ConnectionResultChecks.cs' @('ConnectionLifecycleChecks.cs') 'ConnectionResultChecks'
    Run-Check 'ConnectionResults' $resultChecks @((Join-Path $runRoot 'connection-results.json')) '^PASS (\d+) structured connection result assertions' 174
}
finally { Pop-Location }

Write-Host ('PASS all 11 offline suites: ' + (($summary | Measure-Object -Property Assertions -Sum).Sum) + ' assertions.')
Write-Host ('Generated files are isolated under: ' + $runRoot)

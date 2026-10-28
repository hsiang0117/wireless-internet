param(
    [string]$SourceDirectory = '',
    [switch]$Full,
    [switch]$ModelsOnly,
    [switch]$StaleOnly,
    [switch]$StartupOnly,
    [switch]$PcOnly,
    [switch]$KeepArtifacts
)
if (@($Full, $ModelsOnly, $StaleOnly, $StartupOnly, $PcOnly | Where-Object { $_ }).Count -gt 1) {
    throw 'Choose only one of -Full, -ModelsOnly, -StaleOnly, -StartupOnly, or -PcOnly.'
}
if ($Full) {
    & (Join-Path $PSScriptRoot 'run-full.ps1') -SourceDirectory $SourceDirectory -KeepArtifacts:$KeepArtifacts
    return
}
. (Join-Path $PSScriptRoot 'TestSupport.ps1')
$run = Initialize-TestRun $SourceDirectory 'q' $KeepArtifacts.IsPresent
$succeeded = $false
Push-Location $run.RunRoot
try {
    $references = @('System.Core.dll', 'System.Web.Extensions.dll')
    function Run-PcOnlyChecks {
        $core = @('RuntimeCommon.cs', 'SharedDataModels.cs', 'ConfigurationDocument.cs', 'NativeNetwork.cs',
            'ProcessExecution.cs', 'RuntimeSessionStore.cs', 'OwnedProcessManager.cs', 'AndroidProtocol.cs', 'ActionProtocol.cs')
        $coreReferences = $references + 'System.Management.dll'
        $watchdog = Compile-Check 'WatchdogChecks' ($core + 'WatchdogController.cs') $coreReferences 'regression\WatchdogChecks.cs'
        Run-Check 'Watchdog' $watchdog @('--pc-only') '' 48
        $controllerSources = $core + @('AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs')
        $lifecycle = Compile-Check 'ConnectionLifecycleChecks' $controllerSources $coreReferences 'regression\ConnectionLifecycleChecks.cs'
        Run-Check 'PcOnlyLifecycle' $lifecycle @((Join-Path $run.RunRoot 'pc-only-lifecycle.json'), '--pc-only') '^PASS (\d+) connection lifecycle assertions' 61
        $null = Compile-Check 'ConnectionResultChecks' $controllerSources $coreReferences 'regression\ConnectionResultChecks.cs' @('regression\ConnectionLifecycleChecks.cs') 'ConnectionResultChecks'
    }
    if ($PcOnly) { Run-PcOnlyChecks; $succeeded = $true; return }
    if (-not $ModelsOnly -and -not $StaleOnly -and -not $StartupOnly) {
        $fixture = Compile-Check 'ActionClientFixture' @('ActionProtocol.cs') $references 'focused\ActionClientFixture.cs'
        $sources = @('ActionProtocol.cs', 'WorkerActionClient.cs', 'ProcessExecution.cs', 'Backend.cs', 'MonitoringModels.cs',
            'SharedDataModels.cs', 'SystemSessionObservationSource.cs', 'PhoneTrafficTracker.cs', 'MonitorSnapshotAssembler.cs', 'MemoryLog.cs', 'SessionStatus.cs')
        $checks = Compile-Check 'ActionClientChecks' $sources $references 'focused\ActionClientChecks.cs'
        Run-Check 'ActionClient' $checks @($fixture, $run.RunRoot) '^ActionClientChecks: (\d+) passed' 15
    }
    if (-not $StaleOnly -and -not $StartupOnly) {
        $modelSources = @('SharedDataModels.cs', 'ConfigurationDocument.cs', 'RuntimeCommon.cs', 'RuntimeSessionStore.cs',
            'OwnedProcessManager.cs', 'NativeNetwork.cs', 'ProcessExecution.cs', 'AndroidProtocol.cs',
            'SetupBackend.cs', 'MonitoringModels.cs', 'SystemSessionObservationSource.cs', 'MemoryLog.cs', 'SessionStatus.cs')
        $models = Compile-Check 'SharedModelChecks' $modelSources ($references + 'System.Management.dll') 'focused\SharedModelChecks.cs'
        Run-Check 'SharedModels' $models @($run.RunRoot) '^SharedModelChecks: (\d+) passed' 15
    }
    if (-not $ModelsOnly -and -not $StartupOnly) {
        $staleSources = @('RuntimeCommon.cs', 'SharedDataModels.cs', 'ConfigurationDocument.cs', 'NativeNetwork.cs',
            'ProcessExecution.cs', 'RuntimeSessionStore.cs', 'OwnedProcessManager.cs',
            'AndroidProtocol.cs', 'ActionProtocol.cs', 'AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs')
        $stale = Compile-Check 'StaleSessionChecks' $staleSources ($references + 'System.Management.dll') 'focused\StaleSessionChecks.cs'
        Run-Check 'StaleSessions' $stale @($run.RunRoot) '^StaleSessionChecks: (\d+) passed' 15
    }
    if (-not $ModelsOnly -and -not $StaleOnly) {
        $startupSources = @('RuntimeCommon.cs', 'SharedDataModels.cs', 'ConfigurationDocument.cs', 'NativeNetwork.cs',
            'ProcessExecution.cs', 'RuntimeSessionStore.cs', 'OwnedProcessManager.cs',
            'AndroidProtocol.cs', 'ActionProtocol.cs', 'AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs')
        $startup = Compile-Check 'StartupRecoveryChecks' $startupSources ($references + 'System.Management.dll') 'focused\StartupRecoveryChecks.cs'
        Run-Check 'StartupRecovery' $startup @($run.RunRoot) '^StartupRecoveryChecks: (\d+) passed' 15
    }
    if (-not $ModelsOnly -and -not $StaleOnly -and -not $StartupOnly) { Run-PcOnlyChecks }
    $succeeded = $true
}
finally {
    Pop-Location
    Complete-TestRun $succeeded
}

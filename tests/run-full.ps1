param(
    [string]$SourceDirectory = '',
    [switch]$KeepArtifacts
)
. (Join-Path $PSScriptRoot 'TestSupport.ps1')
$run = Initialize-TestRun $SourceDirectory 'f' $KeepArtifacts.IsPresent
$succeeded = $false
$coreReferences = @('System.Core.dll', 'System.Management.dll', 'System.Web.Extensions.dll')
$coreSources = @('RuntimeCommon.cs', 'SharedDataModels.cs', 'ConfigurationDocument.cs', 'NativeNetwork.cs', 'PhoneGuardSource.cs', 'ProcessExecution.cs', 'RuntimeSessionStore.cs', 'OwnedProcessManager.cs', 'AndroidProtocol.cs', 'ActionProtocol.cs')
$monitorSources = @('Backend.cs', 'MonitoringModels.cs', 'SharedDataModels.cs', 'SystemSessionObservationSource.cs', 'PhoneTrafficTracker.cs', 'MonitorSnapshotAssembler.cs', 'MemoryLog.cs', 'ProcessExecution.cs', 'SessionStatus.cs', 'ActionProtocol.cs', 'WorkerActionClient.cs')
Push-Location $run.RunRoot
try {
    $network = Compile-Check 'NativeNetworkTunRegression' @('NativeNetwork.cs', 'SharedDataModels.cs') $coreReferences 'regression\NativeNetworkTunRegression.cs'
    Run-Check 'Network' $network @() '^PASS (\d+) offline native-network' 69

    $watchdog = Compile-Check 'WatchdogChecks' ($coreSources + @('WatchdogController.cs')) $coreReferences 'regression\WatchdogChecks.cs'
    Run-Check 'Watchdog' $watchdog @() '' 181

    $fixture = Compile-Check 'ProcessExecutionFixture' @() @('System.Core.dll') 'fixtures\ProcessExecutionFixture.cs'
    $process = Compile-Check 'ProcessExecutionChecks' ($coreSources + @('SetupBackend.cs')) $coreReferences 'regression\ProcessExecutionChecks.cs'
    Run-Check 'Process' $process @($fixture) '^PASS (\d+) shared-execution checks' 54

    $monitor = Compile-Check 'MonitoringChecks' $monitorSources @('System.Core.dll', 'System.Web.Extensions.dll') 'regression\MonitoringChecks.cs'
    Run-Check 'Monitoring' $monitor @() '^PASS (\d+) monitoring checks' 62
    $coordinator = Compile-Check 'CoordinatorChecks' @('UiOperationCoordinator.cs') @('System.Core.dll', 'System.Web.Extensions.dll') 'regression\CoordinatorChecks.cs'
    Run-Check 'Coordinator' $coordinator @((Join-Path $run.RunRoot 'coordinator-results.json')) '^PASS (\d+) coordinator checks' 39

    $runtime = Compile-Check 'RuntimeServiceChecks' $coreSources $coreReferences 'regression\RuntimeServiceChecks.cs'
    Run-Check 'RuntimeServices' $runtime @((Join-Path $run.RunRoot 'runtime-services')) '^PASS (\d+) runtime service checks' 51

    $android = Compile-Check 'AndroidClientChecks' ($coreSources + @('AndroidDeviceClient.cs', 'SetupBackend.cs')) $coreReferences 'regression\AndroidClientChecks.cs'
    Run-Check 'Android' $android @() '^PASS (\d+) Android client/protocol checks' 140

    $lifecycle = Compile-Check 'ConnectionLifecycleChecks' ($coreSources + @('AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs')) $coreReferences 'regression\ConnectionLifecycleChecks.cs'
    Run-Check 'Lifecycle' $lifecycle @((Join-Path $run.RunRoot 'lifecycle-results.json')) '^PASS (\d+) connection lifecycle assertions' 264

    $resultChecks = Compile-Check 'ConnectionResultChecks' ($coreSources + @('AndroidDeviceClient.cs', 'ConnectionController.cs', 'ConnectionOperation.cs')) $coreReferences 'regression\ConnectionResultChecks.cs' @('regression\ConnectionLifecycleChecks.cs') 'ConnectionResultChecks'
    Run-Check 'ConnectionResults' $resultChecks @((Join-Path $run.RunRoot 'connection-results.json')) '^PASS (\d+) structured connection result assertions' 174
    $succeeded = $true
}
finally {
    Pop-Location
    Complete-TestRun $succeeded
}

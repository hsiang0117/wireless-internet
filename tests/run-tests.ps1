param(
    [string]$SourceDirectory = '',
    [switch]$Full,
    [switch]$ModelsOnly,
    [switch]$KeepArtifacts
)
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
    if (-not $ModelsOnly) {
        $fixture = Compile-Check 'ActionClientFixture' @('ActionProtocol.cs') $references 'focused\ActionClientFixture.cs'
        $sources = @('ActionProtocol.cs', 'WorkerActionClient.cs', 'ProcessExecution.cs', 'Backend.cs', 'MonitoringModels.cs',
            'SharedDataModels.cs', 'SystemSessionObservationSource.cs', 'PhoneTrafficTracker.cs', 'MonitorSnapshotAssembler.cs', 'MemoryLog.cs', 'SessionStatus.cs')
        $checks = Compile-Check 'ActionClientChecks' $sources $references 'focused\ActionClientChecks.cs'
        Run-Check 'ActionClient' $checks @($fixture, $run.RunRoot) '^ActionClientChecks: (\d+) passed' 15
    }
    $modelSources = @('SharedDataModels.cs', 'ConfigurationDocument.cs', 'RuntimeCommon.cs', 'RuntimeSessionStore.cs',
        'OwnedProcessManager.cs', 'NativeNetwork.cs', 'PhoneGuardSource.cs', 'ProcessExecution.cs', 'AndroidProtocol.cs',
        'SetupBackend.cs', 'MonitoringModels.cs', 'SystemSessionObservationSource.cs', 'MemoryLog.cs', 'SessionStatus.cs')
    $models = Compile-Check 'SharedModelChecks' $modelSources ($references + 'System.Management.dll') 'focused\SharedModelChecks.cs'
    Run-Check 'SharedModels' $models @($run.RunRoot) '^SharedModelChecks: (\d+) passed' 15
    $succeeded = $true
}
finally {
    Pop-Location
    Complete-TestRun $succeeded
}

param(
    [string]$SourceDirectory = '',
    [switch]$Full,
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
    $fixture = Compile-Check 'ActionClientFixture' @('ActionProtocol.cs') $references 'focused\ActionClientFixture.cs'
    $sources = @('ActionProtocol.cs', 'WorkerActionClient.cs', 'ProcessExecution.cs', 'Backend.cs', 'MonitoringModels.cs',
        'SystemSessionObservationSource.cs', 'PhoneTrafficTracker.cs', 'MonitorSnapshotAssembler.cs', 'MemoryLog.cs', 'SessionStatus.cs')
    $checks = Compile-Check 'ActionClientChecks' $sources $references 'focused\ActionClientChecks.cs'
    Run-Check 'ActionClient' $checks @($fixture, $run.RunRoot) '^ActionClientChecks: (\d+) passed' 15
    $succeeded = $true
}
finally {
    Pop-Location
    Complete-TestRun $succeeded
}

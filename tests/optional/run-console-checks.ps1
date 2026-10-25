param(
    [string]$SourceDirectory = '',
    [switch]$KeepArtifacts
)
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'TestSupport.ps1')
$run = Initialize-TestRun $SourceDirectory 'c' $KeepArtifacts.IsPresent
$succeeded = $false
try {
    $executable = Join-Path $run.RunRoot 'ConsoleLogViewChecks.exe'
    $arguments = @('/nologo', '/langversion:5', '/platform:x64', '/target:exe', '/utf8output', '/codepage:65001', ('/out:' + $executable),
        ('/win32manifest:' + (Join-Path $run.Source 'app.manifest')), '/r:System.Core.dll', '/r:System.Drawing.dll',
        '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', (Join-Path $run.Source 'ConsoleLogView.cs'),
        (Join-Path $PSScriptRoot 'ConsoleLogViewChecks.cs'))
    foreach ($message in @(Invoke-CheckedNative $run.Compiler $arguments 'Compile console view checks')) { Write-Host $message }
    Copy-Item -LiteralPath (Join-Path $run.Source 'App.config') -Destination ($executable + '.config')
    Run-Check 'ConsoleLogView' $executable @($run.RunRoot) '^PASS (\d+) console log view checks' 33
    $succeeded = $true
}
finally { Complete-TestRun $succeeded }

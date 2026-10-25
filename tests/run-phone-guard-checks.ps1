param(
    [string]$SourceFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'source\PhoneGuardSource.cs'),
    [string]$BaselineSourceFile = '',
    [string]$BashPath = ''
)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if(-not $BashPath){
    $candidates=@()
    $git=Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if($git){$candidates+=Join-Path (Split-Path -Parent (Split-Path -Parent $git.Source)) 'bin\bash.exe'}
    $candidates+=Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
    $BashPath=@($candidates | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Select-Object -First 1)[0]
}
if(-not $BashPath -or -not (Test-Path -LiteralPath $BashPath -PathType Leaf)){throw 'An existing Git Bash is required; no dependencies will be installed.'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$SourceFile=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceFile)
if($BaselineSourceFile){$BaselineSourceFile=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BaselineSourceFile)}
$runRoot=Join-Path (Join-Path $PSScriptRoot 'bin') ('phone-guard-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $runRoot -Force
$export=Join-Path $PSScriptRoot 'PhoneGuardSourceExport.cs'
function Export-Guard([string]$Source,[string]$Name){
    $executable=Join-Path $runRoot ($Name+'-export.exe')
    $shell=Join-Path $runRoot ($Name+'.sh')
    & $compiler /nologo /warnaserror /langversion:5 /platform:x64 /target:exe /utf8output ('/out:'+$executable) $Source $export
    if($LASTEXITCODE -ne 0){throw ('Guard source compilation failed: '+$Name)}
    & $executable $shell
    if($LASTEXITCODE -ne 0){throw ('Guard source export failed: '+$Name)}
    & $BashPath --noprofile --norc -n $shell
    if($LASTEXITCODE -ne 0){throw ('Guard shell syntax failed: '+$Name)}
    return $shell
}
$new=Export-Guard $SourceFile 'new'
$old='-'
if($BaselineSourceFile){$old=Export-Guard $BaselineSourceFile 'baseline'}
$harness=Join-Path $PSScriptRoot 'PhoneGuardShellChecks.sh'
& $BashPath --noprofile --norc -n $harness
if($LASTEXITCODE -ne 0){throw 'Phone guard harness syntax failed.'}
& $BashPath --noprofile --norc $harness $new $old $runRoot
if($LASTEXITCODE -ne 0){throw 'Isolated phone guard shell checks failed.'}

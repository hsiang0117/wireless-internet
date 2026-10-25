[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SetupPath)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$releaseRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$definition=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$setup=[IO.Path]::GetFullPath($SetupPath)
if(-not (Test-Path -LiteralPath $setup -PathType Leaf)){throw 'Pass the installer produced from the reviewed stage.'}
$target=Join-Path (Join-Path $releaseRoot 'tests') ('install-smoke-'+[guid]::NewGuid().ToString('N'))
$group='WirelessInternet QA '+[guid]::NewGuid().ToString('N')
$groupPath=Join-Path ([Environment]::GetFolderPath('Programs')) $group
$regPath='Software\Microsoft\Windows\CurrentVersion\Uninstall\Local.WirelessInternet.Desktop_is1'
$checks=New-Object Collections.Generic.List[object]
function Check([bool]$Passed,[string]$Name){
    $checks.Add([pscustomobject]@{Name=$Name;Passed=$Passed})
    if(-not $Passed){throw ('FAIL '+$Name)}
    Write-Output ('PASS '+$Name)
}
function Open-UninstallKey {
    foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)){
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
        try {$key=$base.OpenSubKey($regPath); if($key){return $key}} finally {$base.Dispose()}
    }
    return $null
}
function Run-Setup {
    $setupArgs=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=chinesesimp','/TASKS=',('/DIR="'+$target+'"'),('/GROUP="'+$group+'"'))
    $process=Start-Process -FilePath $setup -ArgumentList $setupArgs -WindowStyle Hidden -PassThru
    try {if(-not $process.WaitForExit(30000)){throw 'Only the isolated installer exceeded its 30 second budget.'};return $process.ExitCode} finally {$process.Dispose()}
}
function Run-Uninstall {
    $process=Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
    try {if(-not $process.WaitForExit(30000)){throw 'Only the isolated uninstaller exceeded its 30 second budget.'};return $process.ExitCode} finally {$process.Dispose()}
}
$key=Open-UninstallKey
if($null -ne $key){$key.Dispose();throw 'An existing registered installation exists; refuse to replace its registry entry.'}
# A portable desktop instance uses the same presence marker. Never stop or replace it for QA.
$existingMarker=$null
try {$existingMarker=[Threading.Mutex]::OpenExisting('Local\WirelessInternetDesktopRelease')} catch [Threading.WaitHandleCannotBeOpenedException] {}
if($null -ne $existingMarker){$existingMarker.Dispose();throw 'A desktop application marker already exists. Exit the application normally before running this isolated installer smoke test.'}
if(Test-Path -LiteralPath $target){throw 'The unique test directory already exists.'}
if(Test-Path -LiteralPath $groupPath){throw 'The unique test shortcut directory already exists.'}
if(-not [IO.Path]::GetFullPath($target).StartsWith(([IO.Path]::GetFullPath((Join-Path $releaseRoot 'tests'))+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Test target escaped release workspace.'}
Check ((Run-Setup) -eq 0) 'fresh per-user installation'
$manifest=Get-Content -LiteralPath (Join-Path $releaseRoot 'release-payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedCount=@($definition.SourceFiles).Count+@($definition.DependencyFiles).Count+@($definition.FixedPayloadFiles).Count
Check (@($manifest).Count -eq $expectedCount) 'release manifest matches the declared payload count'
foreach($entry in $manifest){
    $file=Join-Path $target $entry.Path
    if(-not [IO.Path]::GetFullPath($file).StartsWith(([IO.Path]::GetFullPath($target).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'A manifest path escaped the isolated installation.'}
    if(-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $entry.SHA256){throw ('Installed payload mismatch: '+$entry.Path)}
}
Check $true 'all declared payload files match the staged hashes'
$freshScripts=@(Get-ChildItem -LiteralPath $target -File -Recurse | Where-Object {$_.Extension -in @('.ps1','.cmd','.bat','.sh')})
Check ($freshScripts.Count -eq 0) 'fresh native desktop install contains no ps1 cmd bat or sh files'
Check ((Get-Content -LiteralPath (Join-Path $target 'wireless-config.json') -Raw -Encoding UTF8 | ConvertFrom-Json).PhoneIdentity -eq '') 'fresh configuration has no bound phone'
Check (-not (Test-Path -LiteralPath (Join-Path $target 'state')) -and -not (Test-Path -LiteralPath (Join-Path $target 'logs'))) 'no sessions or log directories installed'
$key=Open-UninstallKey
try {Check ($key -and $key.GetValue('DisplayVersion') -eq $definition.Version -and [IO.Path]::GetFullPath($key.GetValue('InstallLocation')).TrimEnd('\') -eq $target) 'per-user uninstall registration points only to test root'} finally {if($key){$key.Dispose()}}
Check ((Test-Path -LiteralPath (Join-Path $groupPath '无线网络共享.lnk')) -and (Test-Path -LiteralPath (Join-Path $groupPath '卸载无线网络共享.lnk'))) 'isolated start-menu launch and uninstall shortcuts'
& (Join-Path $target 'platform-tools\adb.exe') version
Check ($LASTEXITCODE -eq 0) 'packaged ADB dependencies load (version command only)'
$configPath=Join-Path $target 'wireless-config.json'
[IO.File]::WriteAllText($configPath,'{"Transport":"NativeTls","PhoneIdentity":"fixture-phone-ABC123","ConnectEndpoint":"192.0.2.1:37123","DisconnectTimeoutSeconds":30,"WifiInterfaceIndex":0,"EthernetInterfaceIndex":0}',[Text.UTF8Encoding]::new($false))
$configHash=(Get-FileHash -LiteralPath $configPath).Hash
# Simulate only the exact retired components and self-written text documents.
# Third-party NOTICE/LICENSE files and unrelated user files remain untouched.
$obsolete=@('start-wireless-internet.cmd','stop-wireless-internet.cmd','wireless-internet.ps1','wireless-watchdog.ps1','support\gui-action.ps1','support\phone-watchdog.sh','source\build.ps1','使用说明.txt','桌面版使用说明.txt','source\BUILD.txt','notices\第三方组件说明.txt')
foreach($name in $obsolete){[IO.File]::WriteAllText((Join-Path $target $name),'fixture obsolete component - do not execute',[Text.UTF8Encoding]::new($false))}
Check (@($obsolete | Where-Object {Test-Path -LiteralPath (Join-Path $target $_)}).Count -eq $obsolete.Count) 'all exact obsolete files are present before upgrade'
$sentinel=Join-Path $target 'user-kept.txt'
[IO.File]::WriteAllText($sentinel,'fixture untracked user file',[Text.UTF8Encoding]::new($false))
$sentinelHash=(Get-FileHash -LiteralPath $sentinel).Hash
# This unrelated user script appears only after the fresh script-free assertion above.
# Its preservation catches wildcard cleanup of all support scripts instead of exact obsolete names.
$scriptSentinel=Join-Path $target 'support\user-kept.ps1'
[IO.File]::WriteAllText($scriptSentinel,'fixture untracked user script - do not execute',[Text.UTF8Encoding]::new($false))
$scriptSentinelHash=(Get-FileHash -LiteralPath $scriptSentinel).Hash
Check ((Run-Setup) -eq 0) 'upgrade into the same isolated directory'
foreach($name in $obsolete){Check (-not (Test-Path -LiteralPath (Join-Path $target $name))) ('upgrade removes exact obsolete file '+$name)}
Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'upgrade preserves existing user configuration byte for byte'
Check ((Test-Path -LiteralPath $sentinel) -and (Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash) 'upgrade preserves unknown user file byte for byte'
Check ((Test-Path -LiteralPath $scriptSentinel) -and (Get-FileHash -LiteralPath $scriptSentinel).Hash -eq $scriptSentinelHash) 'upgrade preserves unrelated user script rather than deleting by extension'
$stateDir=Join-Path $target 'state'
New-Item -ItemType Directory -Path $stateDir | Out-Null
$stateFile=Join-Path $stateDir 'wireless-session.json'
[IO.File]::WriteAllText($stateFile,'{"FixtureOnly":true}',[Text.UTF8Encoding]::new($false))
$stateHash=(Get-FileHash -LiteralPath $stateFile).Hash
Check ((Run-Setup) -ne 0) 'upgrade refuses a recorded sharing session'
Check ((Run-Uninstall) -ne 0 -and (Test-Path -LiteralPath (Join-Path $target 'WirelessInternet.exe'))) 'uninstall refuses a recorded sharing session'
Check ((Test-Path -LiteralPath $stateFile) -and (Get-FileHash -LiteralPath $stateFile).Hash -eq $stateHash -and (Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'rejected state-protected operations preserve fixture state and user configuration'
# This is the exact fixture written above; no actual runtime process or phone was started.
Remove-Item -LiteralPath $stateFile
Remove-Item -LiteralPath $stateDir
$marker=New-Object Threading.Mutex($false,'Local\WirelessInternetDesktopRelease')
try {
    Check ((Run-Setup) -ne 0) 'upgrade refuses the application presence marker'
    Check ((Run-Uninstall) -ne 0) 'uninstall refuses the application presence marker'
    Check ((Test-Path -LiteralPath (Join-Path $target 'WirelessInternet.exe')) -and (Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'rejected marker-protected operations preserve installed application and user configuration'
} finally {$marker.Dispose()}
Check ((Run-Uninstall) -eq 0) 'normal uninstall after the session has ended'
Check ((Test-Path -LiteralPath $configPath) -and (Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'uninstall preserves the user configuration'
Check ((Test-Path -LiteralPath $sentinel) -and (Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash -and -not (Test-Path -LiteralPath (Join-Path $target 'WirelessInternet.exe'))) 'uninstall removes recorded files and preserves unknown user file bytes'
Check ((Test-Path -LiteralPath $scriptSentinel) -and (Get-FileHash -LiteralPath $scriptSentinel).Hash -eq $scriptSentinelHash) 'uninstall preserves the unrelated user script'
Check (-not (Test-Path -LiteralPath $groupPath)) 'uninstall removes only the test shortcut group'
$key=Open-UninstallKey
try {Check ($null -eq $key) 'temporary uninstall registration is gone'} finally {if($key){$key.Dispose()}}

[pscustomobject]@{Passed=$true;Version=$definition.Version;PayloadFiles=$expectedCount;ObsoleteFiles=$obsolete;Target=$target;Checks=$checks;PhoneOrAdbServerCommandsRun=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseRoot 'installer-smoke-result.json') -Encoding UTF8

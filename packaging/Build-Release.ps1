[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$DependencyDirectory,
    [Parameter(Mandatory=$true)][string]$DocumentationDirectory,
    [string]$StageDirectory,
    [string]$OutputDirectory,
    [string]$MSBuildPath,
    [string]$InnoCompilerPath,
    [string]$ChineseMessagesFile,
    [string[]]$DisallowedText=@(),
    [switch]$SkipBuild,
    [switch]$CompileInstaller
)
$ErrorActionPreference='Stop'
$releaseRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if(-not $SourceDirectory){
    $SourceDirectory=Join-Path $releaseRoot 'source'
    if(-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)){$SourceDirectory=Join-Path $releaseRoot 'gui'}
}
if(-not $StageDirectory){$StageDirectory=Join-Path $releaseRoot 'stage'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $releaseRoot 'artifacts\release'}
$SourceDirectory=[IO.Path]::GetFullPath($SourceDirectory)
$DependencyDirectory=[IO.Path]::GetFullPath($DependencyDirectory)
$DocumentationDirectory=[IO.Path]::GetFullPath($DocumentationDirectory)
$StageDirectory=[IO.Path]::GetFullPath($StageDirectory).TrimEnd('\')
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$allowedStages=@((Join-Path $releaseRoot 'stage'),(Join-Path $releaseRoot 'artifacts\stage'))
if(-not @($allowedStages | Where-Object {[string]::Equals([IO.Path]::GetFullPath($_).TrimEnd('\'),$StageDirectory,[StringComparison]::OrdinalIgnoreCase)}).Count){throw 'Use this checkout''s stage or artifacts\stage directory; a live installation is never a staging target.'}
$definition=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$expected=@($definition.FixedPayloadFiles)+@($definition.DependencyFiles)+@($definition.SourceFiles | ForEach-Object {'source/'+$_})
$expected=@($expected | ForEach-Object {$_.Replace('/','\')} | Sort-Object -Unique)
function RelativeFiles([string]$directory){
    if(-not (Test-Path -LiteralPath $directory)){return @()}
    $prefix=[IO.Path]::GetFullPath($directory).TrimEnd('\')+'\'
    return @(Get-ChildItem -LiteralPath $directory -Recurse -Force -File | ForEach-Object {$_.FullName.Substring($prefix.Length)})
}
function RejectReparse([string]$path){
    $item=Get-Item -LiteralPath $path -Force
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Release inputs and staging files must not be reparse points.'}
}
function CopyPayload([string]$from,[string]$relative){
    if(-not ($expected -contains $relative)){throw ('Unexpected payload destination: '+$relative)}
    RejectReparse $from
    $destination=Join-Path $StageDirectory $relative
    $null=New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath $from -Destination $destination -Force
}
$ancestor=$StageDirectory
while($ancestor -and ($ancestor.StartsWith($releaseRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($ancestor,$releaseRoot,[StringComparison]::OrdinalIgnoreCase))){
    if(Test-Path -LiteralPath $ancestor){RejectReparse $ancestor}
    if([string]::Equals($ancestor,$releaseRoot,[StringComparison]::OrdinalIgnoreCase)){break}
    $ancestor=Split-Path -Parent $ancestor
}
if(Test-Path -LiteralPath $StageDirectory){
    RejectReparse $StageDirectory
    foreach($item in Get-ChildItem -LiteralPath $StageDirectory -Recurse -Force){RejectReparse $item.FullName}
    $unexpected=@(RelativeFiles $StageDirectory | Where-Object {-not ($expected -contains $_)})
    if($unexpected.Count){throw 'Existing staging contains unowned files. Use a fresh isolated staging directory; this script does not delete directories.'}
    $existingConfig=Join-Path $StageDirectory 'wireless-config.json'
    if(Test-Path -LiteralPath $existingConfig){
        $config=Get-Content -LiteralPath $existingConfig -Raw -Encoding UTF8 | ConvertFrom-Json
        $configNames=@($config.PSObject.Properties.Name | Sort-Object)
        $freshNames=@('Transport','PhoneIdentity','DisconnectTimeoutSeconds','ConnectEndpoint','WifiInterfaceIndex','EthernetInterfaceIndex' | Sort-Object)
        if(@(Compare-Object $freshNames $configNames).Count -or $config.Transport -ne 'NativeTls' -or $config.DisconnectTimeoutSeconds -ne 30 -or $config.PhoneIdentity -ne '' -or $config.ConnectEndpoint -ne '' -or $config.WifiInterfaceIndex -ne 0 -or $config.EthernetInterfaceIndex -ne 0){throw 'Existing staging is not the fresh unbound template. It was not overwritten.'}
    }
}
$null=New-Item -ItemType Directory -Path $StageDirectory -Force
RejectReparse $StageDirectory
$binaryDirectory=Join-Path $SourceDirectory 'bin'
if(-not $SkipBuild){
    if(-not $MSBuildPath){
        $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if(Test-Path -LiteralPath $vswhere){$MSBuildPath=@(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1)[0]}
        if(-not $MSBuildPath){$MSBuildPath=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'}
    }
    if(-not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)){throw 'MSBuild is required; this script never installs build tools.'}
    $null=New-Item -ItemType Directory -Path $binaryDirectory -Force
    $icon=Join-Path $DependencyDirectory 'support\wireless.ico'
    if(Test-Path -LiteralPath $icon){Copy-Item -LiteralPath $icon -Destination (Join-Path $binaryDirectory 'wireless.ico') -Force}
    & $MSBuildPath (Join-Path $SourceDirectory 'WirelessInternet.sln') /nologo /m:1 /nr:false /t:Build /v:minimal /p:Configuration=Release /p:Platform=x64 /p:UseSharedCompilation=false
    if($LASTEXITCODE -ne 0){throw 'MSBuild release compilation failed.'}
}
CopyPayload (Join-Path $binaryDirectory 'WirelessInternet.exe') 'WirelessInternet.exe'
CopyPayload (Join-Path $binaryDirectory 'WirelessInternet.exe.config') 'WirelessInternet.exe.config'
foreach($name in @('ConnectionWorker.exe','RelayLogHost.exe')){
    CopyPayload (Join-Path $binaryDirectory $name) ('support\'+$name)
    CopyPayload (Join-Path $binaryDirectory ($name+'.config')) ('support\'+$name+'.config')
}
$icon=Join-Path $binaryDirectory 'wireless.ico'
if(-not (Test-Path -LiteralPath $icon)){$icon=Join-Path $DependencyDirectory 'support\wireless.ico'}
CopyPayload $icon 'support\wireless.ico'
foreach($relative in @('README.md','LICENSE')){CopyPayload (Join-Path $DocumentationDirectory $relative) $relative}
CopyPayload (Join-Path $DocumentationDirectory 'notices\THIRD-PARTY.md') 'notices\THIRD-PARTY.md'
foreach($name in $definition.SourceFiles){CopyPayload (Join-Path $SourceDirectory $name) ('source\'+$name)}
foreach($relative in $definition.DependencyFiles){$relative=$relative.Replace('/','\');CopyPayload (Join-Path $DependencyDirectory $relative) $relative}
$fresh=[ordered]@{Transport='NativeTls';PhoneIdentity='';DisconnectTimeoutSeconds=30;ConnectEndpoint='';WifiInterfaceIndex=0;EthernetInterfaceIndex=0}
[IO.File]::WriteAllText((Join-Path $StageDirectory 'wireless-config.json'),($fresh | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$actual=RelativeFiles $StageDirectory
if(@(Compare-Object $expected ($actual | Sort-Object)).Count){throw 'Staged payload differs from its explicit file allowlist.'}
foreach($item in Get-ChildItem -LiteralPath $StageDirectory -Recurse -Force){RejectReparse $item.FullName}
foreach($relative in $actual){
    $file=Join-Path $StageDirectory $relative
    if([IO.Path]::GetExtension($file) -in @('.ps1','.cmd','.bat','.sh')){throw 'Runtime packages must not contain external scripts.'}
    if([IO.Path]::GetExtension($file) -in @('.cs','.json','.md','.config','.csproj','.sln')){
        $text=[IO.File]::ReadAllText($file)
        if($text -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|github_pat_[A-Za-z0-9_]+|ghp_[A-Za-z0-9]+'){throw ('A credential marker was found in '+$relative)}
        foreach($privateValue in $DisallowedText){if($privateValue -and $text.Contains($privateValue)){throw ('A caller-supplied private value was found in '+$relative)}}
    }
}
$desktopVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $StageDirectory 'WirelessInternet.exe')).FileVersion
if($desktopVersion -ne ($definition.Version+'.0')){throw 'The compiled desktop version differs from the release definition.'}
$manifest=@($actual | Sort-Object | ForEach-Object {[pscustomobject]@{Path=$_;Bytes=(Get-Item -LiteralPath (Join-Path $StageDirectory $_)).Length;SHA256=(Get-FileHash -LiteralPath (Join-Path $StageDirectory $_)).Hash}})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-payload-manifest.json') -Encoding UTF8
if($CompileInstaller){
    if(-not $InnoCompilerPath -or -not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)){throw 'Pass an existing Inno compiler path; this script never downloads or installs a compiler.'}
    $null=New-Item -ItemType Directory -Path $OutputDirectory -Force
    $arguments=@(('/DStageDir='+$StageDirectory),('/DOutputDir='+$OutputDirectory))
    if($ChineseMessagesFile){$arguments+=('/DChineseMessagesFile='+[IO.Path]::GetFullPath($ChineseMessagesFile))}
    $arguments+=(Join-Path $releaseRoot 'installer\WirelessInternet.iss')
    & $InnoCompilerPath @arguments
    if($LASTEXITCODE -ne 0){throw 'Inno compilation failed.'}
    $installer=Join-Path $OutputDirectory ('WirelessInternet-'+$definition.Version+'-Setup-x64.exe')
    ((Get-FileHash -LiteralPath $installer).Hash+'  '+[IO.Path]::GetFileName($installer)) | Set-Content -LiteralPath ($installer+'.sha256') -Encoding ASCII
}
[pscustomobject]@{Version=$definition.Version;PayloadFiles=$manifest.Count;StageDirectory=$StageDirectory;InstallerCompiled=[bool]$CompileInstaller}

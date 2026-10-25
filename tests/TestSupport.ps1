$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Initialize-TestRun([string]$SourceDirectory, [string]$Kind, [bool]$KeepArtifacts) {
    $testRoot = [IO.Path]::GetFullPath($PSScriptRoot)
    if (-not $SourceDirectory) {
        $repository = Split-Path -Parent $testRoot
        foreach ($candidate in @('source', 'gui')) {
            $path = Join-Path $repository $candidate
            if (Test-Path -LiteralPath $path -PathType Container) { $SourceDirectory = $path; break }
        }
    }
    if (-not $SourceDirectory) { throw 'Source directory was not found. Use -SourceDirectory.' }
    $source = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceDirectory))
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'Source directory was not found.' }
    if (-not $env:WINDIR -or -not [Environment]::Is64BitOperatingSystem) { throw 'Checks require Windows x64 and .NET Framework 4.8.' }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The existing .NET Framework x64 compiler was not found; no tools will be downloaded.' }
    $bin = Join-Path $testRoot 'bin'
    $null = New-Item -ItemType Directory -Path $bin -Force
    if ((Get-Item -LiteralPath $bin).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Test output cannot be a reparse point.' }
    $token = [guid]::NewGuid().ToString('N')
    $runRoot = Join-Path $bin ($Kind + '-' + $token.Substring(0, 8))
    $null = New-Item -ItemType Directory -Path $runRoot
    [IO.File]::WriteAllText((Join-Path $runRoot '.test-owner'), $token)
    $script:TestRun = [pscustomobject]@{
        Source = $source; TestRoot = $testRoot; RunRoot = $runRoot; Compiler = $compiler
        Token = $token; KeepArtifacts = $KeepArtifacts; Watch = [Diagnostics.Stopwatch]::StartNew()
        Summary = (New-Object Collections.Generic.List[object])
    }
    return $script:TestRun
}

function Invoke-CheckedNative([string]$Executable, [string[]]$Arguments, [string]$Label) {
    $previousPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 can treat native stderr as errors before reading the exit code.
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
    $executable = Join-Path $script:TestRun.RunRoot ($Name + '.exe')
    $arguments = @('/nologo', '/langversion:5', '/platform:x64', '/target:exe', '/optimize+', '/utf8output', '/codepage:65001', ('/out:' + $executable))
    foreach ($reference in $References) { $arguments += ('/r:' + $reference) }
    if ($EntryPoint) { $arguments += ('/main:' + $EntryPoint) }
    foreach ($name in $Sources) { $arguments += (Join-Path $script:TestRun.Source $name) }
    foreach ($name in (@($TestFile) + $ExtraTests)) { $arguments += (Join-Path $script:TestRun.TestRoot $name) }
    foreach ($message in @(Invoke-CheckedNative $script:TestRun.Compiler $arguments ('Compile ' + $Name))) { Write-Host $message }
    return $executable
}

function Run-Check([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$Pattern, [int]$Expected = 0) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lines = @(Invoke-CheckedNative $Executable $Arguments $Name)
    $text = $lines -join [Environment]::NewLine
    if ($Name -eq 'Watchdog') {
        $report = $lines | Select-Object -Last 1 | ConvertFrom-Json
        if ($report.failures -ne 0 -or $report.realAdbCalls -ne 0) { throw 'Watchdog reported a failure or real ADB call.' }
        $actual = [int]$report.assertions
    }
    else {
        $matched = [regex]::Match($text, $Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
        if (-not $matched.Success) {
            foreach ($line in $lines) { Write-Host $line }
            throw ($Name + ' did not report passing checks.')
        }
        $actual = [int]$matched.Groups[1].Value
    }
    if ($actual -le 0 -or ($Expected -gt 0 -and $actual -ne $Expected)) { throw ($Name + ' reported an unexpected check count: ' + $actual) }
    $script:TestRun.Summary.Add([pscustomobject]@{ Suite = $Name; Assertions = $actual; Passed = $true })
    Write-Host ('PASS ' + $Name + ': ' + $actual + ' checks (' + [Math]::Round($watch.Elapsed.TotalSeconds, 2) + 's)')
}

function Complete-TestRun([bool]$Succeeded) {
    $run = $script:TestRun
    if (-not $run) { return }
    if ($Succeeded) {
        $label = if ($run.Summary.Count -gt 0) { $run.Summary.Count.ToString() + ' suites' } else { 'checks' }
        Write-Host ('PASS ' + $label + ' (' + [Math]::Round($run.Watch.Elapsed.TotalSeconds, 2) + 's including compilation).')
    }
    if (-not $Succeeded -or $run.KeepArtifacts) { Write-Host ('Artifacts retained: ' + $run.RunRoot); return }
    # Remove only this invocation's marked short directory, never old runs or all of bin/.
    $resolved = (Resolve-Path -LiteralPath $run.RunRoot).Path
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $run.TestRoot 'bin'))
    if ((Split-Path -Parent $resolved) -ne $expectedParent -or (Split-Path -Leaf $resolved) -notmatch '^[qfgc]-[0-9a-f]{8}$') { throw 'Refusing test cleanup outside the owned output directory.' }
    if ([IO.File]::ReadAllText((Join-Path $resolved '.test-owner')) -ne $run.Token) { throw 'Test cleanup ownership marker changed.' }
    $items = @((Get-Item -LiteralPath $resolved)) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
    if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Refusing cleanup of a test directory containing a reparse point.' }
    foreach ($process in @(Get-Process)) {
        try { $processPath = $process.Path } catch { $processPath = $null }
        if ($processPath -and $processPath.StartsWith(($resolved + '\'), [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning ('Artifacts retained because an owned test process is still running: ' + $resolved)
            return
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

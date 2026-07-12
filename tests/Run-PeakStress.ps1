#requires -Version 7.0
<#
Runs generated hidden-style stress pools against an already-built RouteZero image.

Quick:
  .\tests\Run-PeakStress.ps1 -Quick

Full:
  .\tests\Run-PeakStress.ps1 -Full

Custom:
  .\tests\Run-PeakStress.ps1 -Count 160 -Seeds 909,1337,2607
#>

[CmdletBinding()]
param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Image = 'routezero:peak',
    [int]$Count = 80,
    [int[]]$Seeds = @(909),
    [string]$WorkRoot = 'E:\act2\stress',
    [string]$Python = 'python',
    [double]$Cpus = 2,
    [string]$Memory = '4g',
    [int]$RunLimitSeconds = 600,
    [switch]$Quick,
    [switch]$Full,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Quick -and $Full) { throw 'Choose either -Quick or -Full.' }
if ($Quick) {
    $Count = 64
    $Seeds = @(909)
}
if ($Full) {
    $Count = 160
    $Seeds = @(909, 1337, 2607)
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI not found.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found.'
}
if (-not (Get-Command $Python -ErrorAction SilentlyContinue)) {
    if (Get-Command python3 -ErrorAction SilentlyContinue) { $Python = 'python3' }
    elseif (Get-Command py -ErrorAction SilentlyContinue) { $Python = 'py' }
    else { throw 'Python interpreter not found (needed to judge generated code answers).' }
}

$poolProject = Join-Path $Repo 'tests\PeakPoolRunner.csproj'
if (-not (Test-Path $poolProject)) { throw "Missing $poolProject" }

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

Write-Host '== build generated-pool tool ==' -ForegroundColor Cyan
dotnet build $poolProject -c Release
if ($LASTEXITCODE -ne 0) { throw "Pool tool build failed: $LASTEXITCODE" }

$allowed = if ([string]::IsNullOrWhiteSpace($env:ALLOWED_MODELS)) {
    'accounts/fireworks/models/minimax-m3,accounts/fireworks/models/kimi-k2p7-code'
} else { $env:ALLOWED_MODELS }

$baseUrl = if ([string]::IsNullOrWhiteSpace($env:FIREWORKS_BASE_URL)) {
    'https://api.fireworks.ai/inference/v1'
} else { $env:FIREWORKS_BASE_URL }

$allRuns = @()
$overallPass = $true

foreach ($seed in $Seeds) {
    $runRoot = Join-Path $WorkRoot "seed-$seed-count-$Count"
    $inputDir = Join-Path $runRoot 'input'
    $outputDir = Join-Path $runRoot 'output'
    $tasksPath = Join-Path $inputDir 'tasks.json'
    $goldPath = Join-Path $runRoot 'gold.json'
    $resultsPath = Join-Path $outputDir 'results.json'
    $dockerLog = Join-Path $runRoot 'docker.log'
    $judgeLog = Join-Path $runRoot 'judge.log'

    Remove-Item $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $inputDir, $outputDir | Out-Null

    Write-Host "`n== generate seed=$seed count=$Count ==" -ForegroundColor Cyan
    dotnet run --project $poolProject -c Release --no-build -- `
        gen --count $Count --seed $seed --out $tasksPath --gold $goldPath
    if ($LASTEXITCODE -ne 0) { throw "Pool generation failed for seed $seed" }

    Write-Host "== run image $Image ==" -ForegroundColor Cyan
    $name = "peak-stress-$seed-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    $sw = [Diagnostics.Stopwatch]::StartNew()

    try {
        $dockerArgs = @(
            'run', '--name', $name,
            '--cpus', ([string]$Cpus),
            '--memory', $Memory,
            '-v', "${inputDir}:/input:ro",
            '-v', "${outputDir}:/output"
        )

        if (-not [string]::IsNullOrWhiteSpace($env:FIREWORKS_API_KEY)) {
            $dockerArgs += @('-e', "FIREWORKS_API_KEY=$env:FIREWORKS_API_KEY")
        }
        $dockerArgs += @(
            '-e', "FIREWORKS_BASE_URL=$baseUrl",
            '-e', "ALLOWED_MODELS=$allowed",
            $Image
        )

        $job = Start-Job -ScriptBlock {
            param($ArgsList)
            & docker @ArgsList 2>&1
            [pscustomobject]@{ ExitCode = $LASTEXITCODE }
        } -ArgumentList (, $dockerArgs)

        if (-not (Wait-Job $job -Timeout $RunLimitSeconds)) {
            docker kill $name 2>$null | Out-Null
            Stop-Job $job -ErrorAction SilentlyContinue
            $dockerOutput = Receive-Job $job -ErrorAction SilentlyContinue
            $dockerOutput | Out-File $dockerLog -Encoding utf8
            throw "TIMEOUT: seed $seed exceeded ${RunLimitSeconds}s"
        }

        $dockerOutput = @(Receive-Job $job)
        $exitRecord = $dockerOutput | Where-Object { $_ -is [psobject] -and $_.PSObject.Properties.Name -contains 'ExitCode' } | Select-Object -Last 1
        $textOutput = @($dockerOutput | Where-Object { $_ -isnot [psobject] -or -not ($_.PSObject.Properties.Name -contains 'ExitCode') })
        $textOutput | Out-File $dockerLog -Encoding utf8
        $exitCode = if ($null -eq $exitRecord) { 1 } else { [int]$exitRecord.ExitCode }
        if ($exitCode -ne 0) { throw "Docker run failed for seed $seed (exit $exitCode). See $dockerLog" }
    }
    finally {
        $sw.Stop()
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        docker rm -f $name 2>$null | Out-Null
    }

    if (-not (Test-Path $resultsPath)) {
        throw "Missing results.json for seed $seed"
    }

    $tasks = @(Get-Content $tasksPath -Raw | ConvertFrom-Json)
    $results = @(Get-Content $resultsPath -Raw | ConvertFrom-Json)
    if ($results.Count -ne $tasks.Count) {
        throw "Coverage failure seed ${seed}: expected $($tasks.Count), got $($results.Count)"
    }

    Write-Host "== judge seed=$seed ==" -ForegroundColor Cyan
    $judgeOutput = & dotnet run --project $poolProject -c Release --no-build -- `
        judge --results $resultsPath --gold $goldPath --python $Python 2>&1
    $judgeOutput | Tee-Object -FilePath $judgeLog | Write-Host

    $headline = ($judgeOutput | Select-String -Pattern 'gen-judge:\s+(\d+)/(\d+)' | Select-Object -First 1)
    if (-not $headline) { throw "Could not parse judge result for seed $seed" }

    $m = [regex]::Match($headline.Line, 'gen-judge:\s+(\d+)/(\d+)')
    $passed = [int]$m.Groups[1].Value
    $total = [int]$m.Groups[2].Value
    $runPass = ($passed -eq $total)

    $remoteIn = 0
    $remoteOut = 0
    $doneLine = Get-Content $dockerLog | Select-String -Pattern 'tokens in=(\d+) out=(\d+)' | Select-Object -Last 1
    if ($doneLine) {
        $dm = [regex]::Match($doneLine.Line, 'tokens in=(\d+) out=(\d+)')
        $remoteIn = [int]$dm.Groups[1].Value
        $remoteOut = [int]$dm.Groups[2].Value
    }

    $run = [pscustomobject]@{
        seed = $seed
        count = $total
        passed = $passed
        accuracy = [Math]::Round(100.0 * $passed / [Math]::Max(1, $total), 2)
        elapsed_seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
        remote_input_tokens = $remoteIn
        remote_output_tokens = $remoteOut
        remote_total_tokens = $remoteIn + $remoteOut
        pass = $runPass
        root = $runRoot
    }
    $allRuns += $run

    $color = if ($runPass) { 'Green' } else { 'Red' }
    Write-Host ("SEED {0}: {1}/{2} ({3}%) | {4}s | remote={5}" -f `
        $seed, $passed, $total, $run.accuracy, $run.elapsed_seconds, $run.remote_total_tokens) -ForegroundColor $color

    if (-not $runPass) { $overallPass = $false }
}

$reportPath = Join-Path $WorkRoot 'peak-stress-report.json'
$allRuns | ConvertTo-Json -Depth 10 | Set-Content $reportPath -Encoding utf8

Write-Host "`n== aggregate ==" -ForegroundColor Cyan
$sumPass = ($allRuns | Measure-Object passed -Sum).Sum
$sumTotal = ($allRuns | Measure-Object count -Sum).Sum
$sumRemote = ($allRuns | Measure-Object remote_total_tokens -Sum).Sum
$maxTime = ($allRuns | Measure-Object elapsed_seconds -Maximum).Maximum
Write-Host "accuracy: $sumPass/$sumTotal"
Write-Host "remote tokens: $sumRemote"
Write-Host "slowest run: ${maxTime}s"
Write-Host "report: $reportPath"

if (-not $overallPass) {
    Write-Host 'OVERALL: FAIL — inspect per-seed judge.log files.' -ForegroundColor Red
    exit 1
}

Write-Host 'OVERALL: PASS' -ForegroundColor Green
exit 0

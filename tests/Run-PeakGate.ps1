[CmdletBinding()]
param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch]$BuildDocker,
    [string]$Image = 'routezero:peak',
    [string]$InputDir = 'E:\act2\smoke\input',
    [string]$OutputDir = 'E:\act2\smoke\output'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-LastExit([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

Push-Location $Repo
try {
    Write-Host '== dotnet build =='
    dotnet build .\act2-harness.csproj -c Release
    Assert-LastExit 'dotnet build'

    Write-Host '== deterministic/self-test =='
    dotnet run --project .\act2-harness.csproj -c Release --no-build -- --self-test
    Assert-LastExit 'Peak self-test'

    if (-not $BuildDocker) {
        Write-Host 'PASS: source build + self-test'
        return
    }

    $required = @(
        '.\zero-parts\llama-server',
        '.\zero-parts\model.gguf'
    )
    foreach ($path in $required) {
        if (-not (Test-Path $path)) { throw "Missing runtime artifact: $path" }
    }

    Write-Host '== docker build =='
    docker build --no-cache -f .\zero\Dockerfile.zero -t $Image .
    Assert-LastExit 'docker build'

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    Remove-Item (Join-Path $OutputDir 'results.json') -Force -ErrorAction SilentlyContinue

    if ([string]::IsNullOrWhiteSpace($env:FIREWORKS_API_KEY)) {
        throw 'FIREWORKS_API_KEY is not set.'
    }
    if (-not (Test-Path (Join-Path $InputDir 'tasks.json'))) {
        throw "Input tasks not found: $(Join-Path $InputDir 'tasks.json')"
    }

    $allowed = if ([string]::IsNullOrWhiteSpace($env:ALLOWED_MODELS)) {
        'accounts/fireworks/models/minimax-m3,accounts/fireworks/models/kimi-k2p7-code'
    } else { $env:ALLOWED_MODELS }
    $baseUrl = if ([string]::IsNullOrWhiteSpace($env:FIREWORKS_BASE_URL)) {
        'https://api.fireworks.ai/inference/v1'
    } else { $env:FIREWORKS_BASE_URL }

    Write-Host '== docker smoke =='
    docker run --rm --cpus=2 --memory=4g `
        -v "${InputDir}:/input:ro" `
        -v "${OutputDir}:/output" `
        -e FIREWORKS_API_KEY=$env:FIREWORKS_API_KEY `
        -e FIREWORKS_BASE_URL=$baseUrl `
        -e ALLOWED_MODELS=$allowed `
        $Image
    Assert-LastExit 'docker smoke'

    $resultsPath = Join-Path $OutputDir 'results.json'
    if (-not (Test-Path $resultsPath)) { throw 'results.json was not written.' }

    $tasks = Get-Content (Join-Path $InputDir 'tasks.json') -Raw | ConvertFrom-Json
    $results = Get-Content $resultsPath -Raw | ConvertFrom-Json
    if ($results.Count -ne $tasks.Count) {
        throw "Task count mismatch: expected $($tasks.Count), got $($results.Count)"
    }
    $expectedIds = @($tasks.task_id | Sort-Object)
    $actualIds = @($results.task_id | Sort-Object)
    if (Compare-Object $expectedIds $actualIds) {
        throw 'Task IDs in results.json do not match tasks.json.'
    }
    if (@($results | Where-Object { [string]::IsNullOrWhiteSpace($_.answer) }).Count -gt 0) {
        throw 'One or more answers are blank.'
    }

    Write-Host "PASS: build, self-test, Docker smoke, schema, and task coverage ($($results.Count)/$($tasks.Count))"
}
finally {
    Pop-Location
}

# R-ZERO bench: build the zero image, run the 32-task dev set under GRADING
# CONSTRAINTS (2 cpu / 4GB), time it, judge it. LOCAL ONLY — nothing pushed.
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
cd E:\act2\repo
docker build -f zero\Dockerfile.zero -t act2-zero-bench .

New-Item -ItemType Directory -Force -Path E:\act2\zero-bench\input, E:\act2\zero-bench\output | Out-Null
Copy-Item E:\act2\act2-ensemble-runner\dev-tasks.json E:\act2\zero-bench\input\tasks.json -Force

$sw = [Diagnostics.Stopwatch]::StartNew()
docker run --rm --cpus=2 --memory=4g `
  -v E:\act2\zero-bench\input:/input:ro -v E:\act2\zero-bench\output:/output `
  -e FIREWORKS_API_KEY=sk-unused -e FIREWORKS_BASE_URL=http://127.0.0.1:9 -e ALLOWED_MODELS=local `
  act2-zero-bench harness --local --comply --in /input/tasks.json --out /output/results.json
$sw.Stop()
Write-Host ("WALL: {0:n0}s of 600s budget" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Cyan

cd E:\act2\act2-ensemble-runner
$env:FIREWORKS_API_KEY = "PASTE-REAL-KEY-FOR-JUDGE"
$env:FIREWORKS_BASE_URL = "https://api.fireworks.ai/inference/v1"
dotnet run --project act2.csproj -c Release -- judge --tasks dev-tasks.json --results E:\act2\zero-bench\output\results.json --judge-model accounts/fireworks/models/gpt-oss-120b

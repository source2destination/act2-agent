# RouteZero Peak build

## Runtime ladder

```text
canonical SHA-256 dedupe
  -> category detection
  -> deterministic closed-space engine
  -> bounded local GGUF attempt
  -> validator
       pass -> answer
       fail -> different strict representation
  -> validator
       pass -> answer
       fail/timeout -> Fireworks emergency fallback
  -> atomic results.json fan-out to every original task_id
```

## Lane map

| Lane | Primary path | Validation |
|---|---|---|
| Math | arithmetic/operator compiler | independent deterministic evaluation |
| Logic | implication, exclusion, ordering, set operators | unique result or abstain |
| Factual | local GGUF | nonblank/direct answer contract |
| Code generation | local GGUF + compact Python examples | function name, structure, Python AST |
| Code debug | local GGUF + compact Python examples | function name, structure, Python AST |
| Sentiment | deterministic polarity evidence | allowed label and requested reason |
| NER | deterministic relation/span extraction, then local fallback | allowed labels and source-grounded spans |
| Summary | compare original/lossless/hybrid/extractive representations | sentence, bullet, and word constraints |

## Peak defaults

`zero/Dockerfile.zero` now runs:

```text
harness --local-fallback --comply --lean-prompts --normalize-input
```

No category is forced remote. The remote ladder remains available only after deterministic/local paths fail.

## Build

The real repo must contain the existing local runtime payload:

```text
zero-parts/llama-server
zero-parts/*.so*
zero-parts/model.gguf
```

Build:

```powershell
docker build --no-cache `
  -f .\zero\Dockerfile.zero `
  -t routezero:peak `
  .
```

Source self-test:

```powershell
dotnet build .\act2-harness.csproj -c Release
dotnet run --project .\act2-harness.csproj -c Release --no-build -- --self-test
```

Full smoke run:

```powershell
docker run --rm --cpus=2 --memory=4g `
  -v E:\act2\smoke\input:/input:ro `
  -v E:\act2\smoke\output:/output `
  -e FIREWORKS_API_KEY=$env:FIREWORKS_API_KEY `
  -e FIREWORKS_BASE_URL=https://api.fireworks.ai/inference/v1 `
  -e ALLOWED_MODELS=accounts/fireworks/models/minimax-m3,accounts/fireworks/models/kimi-k2p7-code `
  routezero:peak

Get-Content E:\act2\smoke\output\results.json
```

## Promotion gate

```text
build succeeds
self-test passes
retired 10/10
current 16/16
all task IDs present
valid JSON
runtime under event limit
remote tokens lower than safe backup
```

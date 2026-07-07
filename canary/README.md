# RouteZero Canary — Reference Forwarder

**Team RLSym · AMD Developer Hackathon ACT II · Track 1**

This is a reference implementation: unmodified prompts forwarded to the first
model in the injected `ALLOWED_MODELS` via `FIREWORKS_BASE_URL`, temperature 0,
no retries, no routing or reduction strategy. It exists to verify the
evaluation pipeline.

## Behavior

- Reads `/input/tasks.json` on startup
- Logs the injected `ALLOWED_MODELS` value verbatim
- Model selection rule: **first model in the injected list**
- One `chat/completions` call per task, user message = the task prompt byte-identical, `temperature: 0`
- No retries. A failed call logs the error and emits an empty answer for that `task_id`
- Concurrent execution (8 workers), per-request timeout 25s, total budget 9 minutes
- Writes `/output/results.json` atomically — every `task_id` present, input order preserved, validated JSON
- Exits 0 on success
- Logs per task: `task_id`, HTTP status, model, client-side prompt/completion token counts, latency

## Contract compliance

| Requirement | Status |
|---|---|
| Reads env `FIREWORKS_API_KEY` / `FIREWORKS_BASE_URL` / `ALLOWED_MODELS` at runtime | Yes — nothing hardcoded, no bundled `.env` |
| All inference via `FIREWORKS_BASE_URL` | Yes — sole network destination |
| Only `ALLOWED_MODELS` models | Yes — selected from the injected list at runtime |
| Valid `/output/results.json` | Yes — atomic write, schema-checked, all task_ids |
| Exit code 0 / non-zero | Yes |
| Startup < 60s | Yes — slim image, no model weights |
| Per-request < 30s | Yes — 25s client timeout |
| Total runtime < 10 min | Yes — 9 min budget |
| linux/amd64 | Yes — see build command |
| Image < 10GB compressed | Yes — ~50MB |

## Build & run

```bash
docker buildx build --platform linux/amd64 -t <registry>/routezero-canary:latest --push .

docker run -v $(pwd)/input:/input -v $(pwd)/output:/output \
  -e FIREWORKS_API_KEY="..." \
  -e FIREWORKS_BASE_URL="https://api.fireworks.ai/inference/v1" \
  -e ALLOWED_MODELS="accounts/fireworks/models/<model-id>" \
  <registry>/routezero-canary:latest
```

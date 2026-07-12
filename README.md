# RouteZero — AMD ACT II, Track 1

Token-efficient routing agent, built solo under handle **RLSym**. One container, one contract:

```text
/input/tasks.json -> RouteZero -> /output/results.json
```

Final evaluated container: `ghcr.io/source2destination/act2-agent:v3`
Digest: `sha256:ff72e50fa8349cfb26f84147c70084ac0c4c1f323b41e077601ab9a4c6b57245`

## Results (all measured, all reproducible)

| Instrument | Result |
|---|---|
| Organizer-published retired validation set | **10/10**, graded against the published expected-answer criteria |
| Independently seeded generated pools (3 seeds, incl. 2 never used during tuning) | **384/384** |
| Dead-proxy degradation (remote endpoint unreachable) | valid output, **64/64 task IDs**, zero blanks, no hang |
| Third-party leaderboard score (prior build of this pipeline) | 94.7% accuracy, scored 2026-07-12 |
| Blank answers across ~4,000 logged task executions | **0** |

The retired-set result above was produced by pulling this exact GHCR digest and running it under the published grading constraints (2 vCPU / 4 GB) — not a local build. Reproduce it with the Quickstart below.

## How it works

Deterministic-and-local-first ladder; the paid remote lane exists only as accuracy insurance:

```text
canonical prompt dedupe (SHA-256, answer fan-out)
  -> category detection
  -> deterministic closed-space engines (math, logic, sentiment, NER)
  -> bounded local GGUF attempt (in-container, CPU)
  -> strict validator (format + content contracts per category)
       pass -> answer
       fail -> retry with a different strict representation
       fail -> Fireworks escalation (emergency lane)
  -> atomic results.json, every task_id always present
```

On the organizer's retired set, 9 of 10 tasks resolve at **zero remote tokens**; one summarization task tripped the sentence-count validator locally and escalated (277 tokens). That validation-gated escalation is the design working as intended: tokens are spent only when a local answer provably fails its contract.

Lane details, promotion gates, and build steps: [`PEAK_BUILD.md`](PEAK_BUILD.md).

## AMD / Fireworks resource usage

- **Fireworks AI serverless inference** is the remote escalation lane (`accounts/fireworks/models/minimax-m3`; `kimi-k2p7-code` allowed for code lanes). Hackathon-provided credits were used for all remote calls, development and graded.
- All results were measured under the event's published grading constraints: **2 vCPU / 4 GB**, linux/amd64.
- Local inference runs a quantized Qwen2.5-3B GGUF entirely in-container on CPU (permitted per the participant guide; token efficiency is scored on remote usage, and correctness is scored regardless of where the answer was produced).
- Configuration is read at runtime from `FIREWORKS_API_KEY`, `FIREWORKS_BASE_URL`, and `ALLOWED_MODELS`. **No secrets are stored in the image or this repository.**

## Quickstart

Run the exact graded artifact:

```powershell
docker run --rm --cpus=2 --memory=4g \
  -v <your-input-dir>:/input:ro \
  -v <your-output-dir>:/output \
  -e FIREWORKS_API_KEY=<your_key> \
  -e FIREWORKS_BASE_URL=https://api.fireworks.ai/inference/v1 \
  -e ALLOWED_MODELS=accounts/fireworks/models/minimax-m3,accounts/fireworks/models/kimi-k2p7-code \
  ghcr.io/source2destination/act2-agent:v3
```

`/input/tasks.json` is a JSON array of `{"task_id": "...", "prompt": "..."}` objects; results land in `/output/results.json` in the same shape with `answer` fields.

Build from source: fetch the local runtime payload first (`zero/get-zero-parts.ps1`), then follow [`PEAK_BUILD.md`](PEAK_BUILD.md).

## Repository layout

```text
src/                  main code path
  Harness.cs            entry: routing, lanes, ladders (start here)
  PeakDeterministic.cs  closed-space engines (math, logic, sentiment, NER)
  PeakPreprocess.cs     dedupe, normalization, input handling
  Comply.cs             format validators + repair (bullets, sentences, word caps)
  Solvers.cs/Solvers2.cs deterministic arithmetic families
  SummaryReducer.cs     summary representation comparison
  PoolGen.cs, Judge.cs  evaluation toolkit: seeded pool generation + LLM judge
tests/                Run-PeakGate.ps1 (build + self-test + smoke), Static-Audit.py
canary/               diagnostic sidecar built during the event to isolate a
                      platform-side scoring outage (adopted by other participants)
zero/                 Dockerfile.zero, entrypoint, payload fetch script
```

## Evaluation methodology

Every promoted build had to clear, in order: compile + 22 self-tests, the organizer-published retired set at 10/10 (hand-graded against the published criteria), a 64-task generated regression pool, two 160-task pools on fresh seeds never used during tuning, and a dead-proxy run proving valid degraded output with the remote lane unreachable. The generation/judging toolkit that enforces this is in this repository (`PoolGen.cs`, `Judge.cs`, `tests/Run-PeakGate.ps1`).

## External services

Fireworks AI inference API only. Nothing else is called at runtime.

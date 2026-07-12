# RLSym — Hybrid Local-First Routing Agent

AMD ACT II · Track 1

RLSym is a hybrid local-first routing agent covering all eight Track 1 task categories. It uses deterministic engines whenever a task can be solved exactly, a quantized local 3B model when interpretation is required, and Fireworks only as an emergency accuracy fallback after local validation fails.

The final submitted container was validated under the published grading constraints (`--cpus=2 --memory=4g`) and then pulled back from GHCR for one last organizer-set run. The pulled artifact matched the exact locally tested binary.

## Final measured results

| Metric | Final result |
|---|---:|
| Organizer-authored retired set | **10/10 — 100%** |
| Generated regression seed 909 | **64/64 — 100%** |
| Fresh generated seed 1337 | **160/160 — 100%** |
| Fresh generated seed 2607 | **160/160 — 100%** |
| Generated-task aggregate | **384/384 — 100%** |
| Blank answers across final gates | **0** |
| Remote tokens, 64-task seed 909 | **266** |
| Remote tokens, fresh 320-task aggregate | **1,249** |
| Remote tokens, all 384 generated tasks | **1,515** (~3.95/task) |
| Worst observed runtime | **155.2 seconds for 160 tasks** |
| Dead-provider coverage test | **64/64 task IDs returned, twice** |
| Dead-provider runtimes | **103.4 s and 102.7 s** |
| Deterministic/self-test suite | **22/22** |
| Docker smoke test | **16/16** |
| Final GHCR pull-back | **10/10, zero blanks** |
| Local model | Qwen2.5-3B-Instruct, Q4_K_M |
| Local inference runtime | llama.cpp, CPU-only |
| Container platform | linux/amd64 |
| Runtime constraint | Verified at 2 vCPU / 4 GB |
| Submitted image | `ghcr.io/source2destination/act2-agent:v3` |
| Recovery tag | `ghcr.io/source2destination/act2-agent:peak-retired10` |
| Submitted digest | `sha256:ff72e50fa8349cfb26f84147c70084ac0c4c1f323b41e077601ab9a4c6b57245` |

The exact DLL extracted from the pushed GHCR image matched the DLL from the locally tested image:

```text
1FA83360258F7684DA9BE7EDBBA066FCFCE793718A6B46861C21EEDCCD06FCA4
```

## Architecture

```text
task batch
  → exact deduplication and safe normalization
  → negation-hardened task classifier
  → deterministic closed-space engines          0 scored tokens
  → local Qwen2.5-3B through llama.cpp           0 scored tokens
  → output validation
  → representation-changing local retry
  → selective Fireworks emergency fallback
  → format compliance and never-blank guard
  → /output/results.json
```

### Classifier

Classification is rule-based and local. It is hardened against negation, task-word decoys, and misleading category terms. A prompt such as:

> This is not a sentiment classification. What is the chemical formula of water?

routes to the factual lane rather than sentiment merely because the word “sentiment” appears.

### Deterministic engines

Closed-form tasks are solved without model inference when the prompt can be parsed safely. Final deterministic coverage includes:

- arithmetic and percentage transformations
- reverse percentages, inventory flow, ratios, costs, and compound interest
- implication chains, comparison graphs, elimination, and exactly-one logic
- contrastive sentiment with evidence from both sides
- relation-based entity extraction with grounded source spans
- common Python debugging families
- selected high-confidence factual relationships

The engines are precision-first: when parsing is uncertain, they decline and pass the task to the next route instead of manufacturing a confident answer.

### Local model

RLSym bakes Qwen2.5-3B-Instruct Q4_K_M into the container and serves it through llama.cpp. Local inference runs entirely inside the grading container and requires no GPU.

The local path uses compact category-specific prompts, validation, and a second attempt that changes the answer representation instead of resending the same failed prompt. For code tasks, extracted Python is executed against assertions when tests are available.

### Summary routing

Summarization evaluates multiple safe input representations:

- original text
- lossless phrase factoring
- hybrid structure
- extractive representation

The selector balances token savings against semantic coverage and format risk. Sentence counts, bullet counts, and word limits are validated after generation. A rejected local result may be retried with a stricter representation before remote escalation.

### Fireworks fallback

No category is permanently forced remote. Fireworks is used only when deterministic and local routes cannot produce a validated answer.

All remote traffic uses `FIREWORKS_BASE_URL`, and available models are read from `ALLOWED_MODELS`. The final generated-task aggregate used 1,515 remote tokens across 384 tasks while preserving 100% measured accuracy.

### Compliance and resilience

The compliance layer validates and repairs:

- sentence-count requirements
- bullet counts and bullet-length ceilings
- JSON structure
- named-entity grounding
- code extraction and syntax
- label restrictions
- required output shape

RLSym preserves every task ID, fans deduplicated answers back to duplicate IDs, and never intentionally emits a blank result. Two complete runs against an unreachable Fireworks proxy still produced valid output containing all 64 task IDs without hanging.

## Validation matrix

The final build passed four independent gate types before submission:

### 1. Organizer-authored retired set

The public retired set is the closest available organizer-authored instrument. RLSym passed all ten tasks under the exact 2-vCPU, 4-GB runtime constraint.

The set covered:

- RGB versus RYB
- machine learning versus deep learning
- RAM versus ROM
- inventory arithmetic
- recipe scaling and cost
- contrastive sentiment with both-side reasoning
- exact two-sentence summarization
- three short bullets
- five-entity JSON extraction

### 2. Generated regression and fresh-seed pools

The final image passed:

- seed 909: 64/64
- seed 1337: 160/160
- seed 2607: 160/160

All eight categories were perfect on both fresh seeds. The fresh seeds were run after the initial tuning seed to test whether the deterministic families generalized beyond the examples used during development.

### 3. Remote-outage tests

The Fireworks endpoint was replaced with an unreachable local address. The final image completed the entire 64-task batch twice:

- 64/64 task IDs returned
- valid `results.json`
- zero blanks
- no hang
- approximately 103 seconds per run

### 4. Registry pull-back verification

The final image was pushed with provenance and SBOM attestations disabled to avoid unsupported multi-manifest shapes. The exact `v3` tag was pulled back from GHCR, its DLL hash matched the locally tested DLL, and the pulled image passed the organizer-authored retired set 10/10.

## Submission constraints

- Reads `/input/tasks.json`
- Writes `/output/results.json`
- Exits successfully after producing complete output
- Uses only `FIREWORKS_BASE_URL` for Fireworks traffic
- Calls only models supplied through `ALLOWED_MODELS`
- Makes no unrelated outbound network calls
- Runs local inference entirely inside the container
- Uses deterministic decoding
- Contains no task-ID keyed answer cache
- Uses semantic rules rather than cached benchmark outputs
- Runs on linux/amd64
- Verified under 2 vCPU and 4 GB memory
- Local model copied once into its final image layer

## Reproducibility identifiers

| Artifact | Identifier |
|---|---|
| Submission image | `ghcr.io/source2destination/act2-agent:v3` |
| Immutable recovery tag | `ghcr.io/source2destination/act2-agent:peak-retired10` |
| Registry digest | `sha256:ff72e50fa8349cfb26f84147c70084ac0c4c1f323b41e077601ab9a4c6b57245` |
| Tested/pushed DLL SHA-256 | `1FA83360258F7684DA9BE7EDBBA066FCFCE793718A6B46861C21EEDCCD06FCA4` |
| Entrypoint SHA-256 | `85438DC94EA69953EC647FF8ACFC9FE43EDCDB5565A0939D9B9BE90FA7C6320A` |
| Tested local image ID | `sha256:5749ca7ed502a2fa9f3a8bcc16a0b29fe5f8402a27890b633df499bad405dbc3` |

## Scope note

This entry intentionally uses generic, publicly reproducible techniques. Separate proprietary context-efficiency and lossless-reduction systems were excluded from the public submission and can be discussed privately.

---

Built solo · RLSym

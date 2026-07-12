# RouteZero — ACT II Track 1

Hybrid token-efficient routing agent for the AMD Developer Hackathon ACT II.

```text
/input/tasks.json -> RouteZero -> /output/results.json
```

Peak routing is deterministic/local-first:

- canonical SHA-256 prompt dedupe with answer fan-out to every task ID;
- arithmetic and symbolic-logic engines for closed task spaces;
- local Qwen GGUF generation for factual, code, NER, sentiment, and summaries;
- compact Python syntax examples plus Python AST validation;
- validator-triggered retries that change the required representation;
- 2–3 reduced summary representations estimated before local generation;
- Fireworks routing retained only as emergency accuracy insurance;
- atomic, complete `results.json` writes throughout execution.

Configuration is read at runtime from `FIREWORKS_API_KEY`, `FIREWORKS_BASE_URL`, and `ALLOWED_MODELS`. No secrets are stored in the image.

Build and gate instructions: [`PEAK_BUILD.md`](PEAK_BUILD.md).

"""
RouteZero Canary — Reference Forwarder
AMD Developer Hackathon ACT II, Track 1

Purpose: verify the evaluation pipeline. Forwards each task prompt
byte-identical to the first model in ALLOWED_MODELS via
FIREWORKS_BASE_URL, temperature 0, no system prompt, no retries.
Contains zero routing/reduction strategy by design.
"""

import concurrent.futures
import json
import os
import sys
import threading
import time

import requests

REQUEST_TIMEOUT_S = 25          # per-request wall clock, under the 30s rule
TOTAL_BUDGET_S = 540            # 9 min, margin under the 10 min rule
MAX_WORKERS = 8

START = time.monotonic()


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}][+{time.monotonic() - START:7.2f}s] {msg}", flush=True)


def read_tasks() -> list:
    for path in ("/input/tasks.json", "./input/tasks.json"):
        if os.path.exists(path):
            log(f"reading tasks from {path}")
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
    log("FATAL: no tasks.json found at /input/tasks.json or ./input/tasks.json")
    sys.exit(1)


def main() -> None:
    api_key = os.environ["FIREWORKS_API_KEY"]
    base_url = os.environ["FIREWORKS_BASE_URL"].rstrip("/")
    allowed_raw = os.environ["ALLOWED_MODELS"]

    # Log the injected list verbatim — primary telemetry of this canary.
    log(f"ALLOWED_MODELS (verbatim): {allowed_raw!r}")

    models = [m.strip() for m in allowed_raw.split(",") if m.strip()]
    if not models:
        log("FATAL: ALLOWED_MODELS is empty")
        sys.exit(1)

    # Selection rule (documented in README): first model in the injected list.
    model = models[0]
    log(f"selected model: {model} (rule: first in list)")
    log(f"base_url: {base_url}")

    tasks = read_tasks()
    log(f"task count: {len(tasks)}")

    session = requests.Session()
    session.headers.update(
        {"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"}
    )

    totals = {"in": 0, "out": 0, "ok": 0, "fail": 0}
    totals_lock = threading.Lock()

    def forward(task: dict) -> dict:
        task_id = task.get("task_id", "")
        prompt = task.get("prompt", "")
        t0 = time.monotonic()
        answer = ""
        status = "FAIL"
        usage_in = usage_out = 0
        http = "-"
        try:
            resp = session.post(
                f"{base_url}/chat/completions",
                json={
                    "model": model,
                    "messages": [{"role": "user", "content": prompt}],
                    "temperature": 0,
                },
                timeout=REQUEST_TIMEOUT_S,
            )
            http = str(resp.status_code)
            resp.raise_for_status()
            data = resp.json()
            answer = data["choices"][0]["message"]["content"] or ""
            usage = data.get("usage", {})
            usage_in = usage.get("prompt_tokens", 0)
            usage_out = usage.get("completion_tokens", 0)
            status = "OK"
        except Exception as e:  # no retries by design; log and emit empty answer
            log(f"task {task_id}: ERROR {type(e).__name__}: {e}")
        dt = time.monotonic() - t0
        log(
            f"task {task_id}: {status} http={http} model={model} "
            f"tok_in={usage_in} tok_out={usage_out} latency={dt:.2f}s"
        )
        with totals_lock:
            totals["in"] += usage_in
            totals["out"] += usage_out
            totals["ok" if status == "OK" else "fail"] += 1
        return {"task_id": task_id, "answer": answer}

    results = []
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=MAX_WORKERS)
    futures = [pool.submit(forward, t) for t in tasks]
    try:
        for fut in concurrent.futures.as_completed(
            futures, timeout=max(5, TOTAL_BUDGET_S - (time.monotonic() - START))
        ):
            results.append(fut.result())
    except concurrent.futures.TimeoutError:
        # budget expired: salvage everything finished, emit "" for the rest —
        # a partial results file scores its answers; a crash scores zero.
        log(f"BUDGET EXPIRED after {time.monotonic() - START:.1f}s — salvaging partial results")
        for fut in futures:
            if fut.done() and not fut.cancelled():
                r = fut.result()
                if r not in results:
                    results.append(r)
            else:
                fut.cancel()
    finally:
        pool.shutdown(wait=False, cancel_futures=True)

    # Preserve input order; every task_id present exactly once.
    order = {t.get("task_id", ""): i for i, t in enumerate(tasks)}
    results.sort(key=lambda r: order.get(r["task_id"], 1 << 30))
    seen = {r["task_id"] for r in results}
    for t in tasks:
        tid = t.get("task_id", "")
        if tid not in seen:
            results.append({"task_id": tid, "answer": ""})

    # Atomic write: full structure built, validated, single replace.
    out_dir = "/output" if os.path.isdir("/output") else "./output"
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "results.json")
    tmp_path = out_path + ".tmp"
    payload = json.dumps(results, ensure_ascii=False, indent=2)
    json.loads(payload)  # schema sanity: must be valid JSON before it touches disk
    with open(tmp_path, "w", encoding="utf-8") as f:
        f.write(payload)
    os.replace(tmp_path, out_path)

    log(
        f"done: {totals['ok']} ok, {totals['fail']} failed | "
        f"client-side tokens in={totals['in']} out={totals['out']} total={totals['in'] + totals['out']} | "
        f"wrote {out_path} ({len(results)} entries) | runtime {time.monotonic() - START:.1f}s"
    )
    sys.exit(0)


if __name__ == "__main__":
    main()

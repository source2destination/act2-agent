using System.Text;
using System.Text.Json;
using Act2;

// ── ACT II ensemble runner (block one) ───────────────────────────────────────
// Runs a prompt slice through N local models ONE AT A TIME (sequential, fits 16GB),
// capturing answer + logprobs + embedding for each (prompt, model), and persists
// everything to JSONL. The deviation layer (block two) reads that file offline, so
// embedding-vs-logit-vs-text is decided by results, not chosen here. Scales to the
// 192GB env by flipping --concurrent (leave models loaded; skip per-model unload).
//
// Usage:
//   ensemble-runner --prompts prompts.jsonl --out outputs.jsonl \
//       --models "llama-3.2-3b-instruct,qwen3.5-4b,gpt-oss-20b" \
//       --embed-model nomic-embed-text --limit 300 --top-logprobs 5
//   ensemble-runner --dry --out /tmp/dry.jsonl --limit 20      (no server; self-test)
//   ensemble-runner analyze --in outputs.jsonl --prompts prompts.jsonl [--grid 12]

if (args.Length > 0 && args[0] == "analyze")
{
    string inPath = "outputs.jsonl", pPath = "prompts.jsonl"; int grid = 12;
    for (int i = 1; i < args.Length; i++)
    {
        string k = args[i];
        string NV() => i + 1 < args.Length ? args[++i] : "";
        switch (k)
        {
            case "--in": inPath = NV(); break;
            case "--prompts": pPath = NV(); break;
            case "--grid": int.TryParse(NV(), out grid); break;
            default: Console.Error.WriteLine($"unknown analyze arg: {k}"); return 1;
        }
    }
    return Analyzer.Run(inPath, pPath, grid);
}

if (args.Length > 0 && args[0] == "classify")
{
    string pPath = "prompts.jsonl"; int samples = 2;
    for (int i = 1; i < args.Length; i++)
    {
        string k = args[i];
        string NV() => i + 1 < args.Length ? args[++i] : "";
        switch (k)
        {
            case "--prompts": pPath = NV(); break;
            case "--samples": int.TryParse(NV(), out samples); break;
            case "--selftest": return Classifier.SelfTest();
            default: Console.Error.WriteLine($"unknown classify arg: {k}"); return 1;
        }
    }
    return Classifier.Report(pPath, samples);
}

if (args.Length > 0 && args[0] == "grade-check") return GradeCheck.Run();

if (args.Length > 0 && args[0] == "solve-check")
{
    // solver-lane selftest: must fire correctly on clean asks, never on word problems
    var cases = new (string p, string? want)[] {
        ("What is 17 * 24 + 3?", "411"),
        ("Calculate 128/4 - 7.", "25"),
        ("What is 20% of 250?", "50"),
        ("What is (8 + 2) * 6?", "60"),
        ("what is 3.5 * 4?", "14"),
        // must NOT fire:
        ("Natalia sold clips to 48 of her friends in April, and then she sold half as many in May. How many altogether?", null),
        ("A store cuts a $250 jacket's price by 20%, then adds 8% sales tax. What is the final cost?", null),
        ("What is the capital of France?", null),
        ("What is 20% of the revenue described above?", null),
    };
    int sfail = 0;
    foreach (var (p, want) in cases)
    {
        bool fired = Solvers.TrySolve(p, out string got);
        bool ok = want == null ? !fired : (fired && got == want);
        if (!ok) { sfail++; Console.WriteLine($"FAIL: \"{p}\" -> fired={fired} got=\"{got}\" want={(want ?? "<no-fire>")}"); }
        else Console.WriteLine($"pass: {(want == null ? "no-fire" : got),-8} {p[..Math.Min(60, p.Length)]}");
    }
    Console.WriteLine(sfail == 0 ? "solve-check: ALL PASS" : $"solve-check: {sfail} FAIL");
    return sfail == 0 ? 0 : 1;
}

if (args.Length > 0 && args[0] == "gen") return PoolGen.RunGen(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "gen-judge") return PoolGen.RunJudge(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "judge")
{
    // LLM-judge our results against dev tasks — the internal gate proxy.
    string jt = "dev-tasks.json", jr = "", jm = "accounts/fireworks/models/gpt-oss-120b";
    string? jout = null; int jpar = 6; bool jstrict = false;
    for (int i = 1; i < args.Length; i++)
    {
        string k = args[i];
        string NV() => i + 1 < args.Length ? args[++i] : "";
        switch (k)
        {
            case "--tasks": jt = NV(); break;
            case "--results": jr = NV(); break;
            case "--judge-model": jm = NV(); break;
            case "--out": jout = NV(); break;
            case "--parallel": int.TryParse(NV(), out jpar); break;
            case "--strict": jstrict = true; break;
            default: Console.Error.WriteLine($"unknown judge arg: {k}"); return 1;
        }
    }
    if (jr.Length == 0) { Console.Error.WriteLine("judge: --results <results.json> required"); return 1; }
    return await Judge.RunAsync(jt, jr, jm, jout, jpar, jstrict);
}

if (args.Length > 0 && args[0] == "fw-list")
{
    // discover deployed model ids from the live API: no guessing serving paths.
    // usage: fw-list [--grep substr]   (FIREWORKS_API_KEY from env)
    string? grep = null;
    for (int i = 1; i < args.Length; i++)
        if (args[i] == "--grep" && i + 1 < args.Length) grep = args[++i];
    try
    {
        var fw = new FireworksBackend();
        var ids = await fw.ListModelsAsync(CancellationToken.None);
        var hits = ids.Where(m => grep == null || m.Contains(grep, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) { Console.WriteLine($"no models matched '{grep}' ({ids.Length} total listed)"); return 1; }
        foreach (var m in hits) Console.WriteLine(m);
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine("fw-list: " + ex.Message); return 1; }
}

if (args.Length > 0 && args[0] == "harness")
{
    // Track 1 container contract: /input/tasks.json -> /output/results.json.
    // Overrides exist for local dev only; the graded container runs bare `harness`.
    string? hin = null, hout = null, hmodel = null;
    int par = 8, perTask = 25, wallS = 540;   // 9-min wall inside the 10-min limit
    bool useSolver = true;
    for (int i = 1; i < args.Length; i++)
    {
        string k = args[i];
        string NV() => i + 1 < args.Length ? args[++i] : "";
        switch (k)
        {
            case "--in": hin = NV(); break;
            case "--out": hout = NV(); break;
            case "--model": hmodel = NV(); break;
            case "--parallel": int.TryParse(NV(), out par); break;
            case "--per-task-seconds": int.TryParse(NV(), out perTask); break;
            case "--wall-seconds": int.TryParse(NV(), out wallS); break;
            case "--no-solver": useSolver = false; break;
            default: Console.Error.WriteLine($"unknown harness arg: {k}"); return 1;
        }
    }
    return await Harness.RunAsync(hin, hout, par, perTask, wallS, hmodel, useSolver);
}

if (args.Length > 0 && args[0] == "costsweep")
{
    string inPath = "rb-free.jsonl", pPath = "rb200.jsonl", remoteKind = "dry";
    string remoteModel = "accounts/fireworks/models/llama-v3p1-8b-instruct";
    double embW = 0.0; int lim = 0; int rMax = 2048; string? rReason = null; bool dumpR = false; bool baselines = false;
    double[] thr = { 1.0, 0.9, 0.75, 0.6, 0.5 };
    for (int i = 1; i < args.Length; i++)
    {
        string k = args[i];
        string NV() => i + 1 < args.Length ? args[++i] : "";
        switch (k)
        {
            case "--in": inPath = NV(); break;
            case "--prompts": pPath = NV(); break;
            case "--remote": remoteKind = NV(); break;
            case "--remote-model": remoteModel = NV(); break;
            case "--embed-weight": double.TryParse(NV(), out embW); break;
            case "--limit": int.TryParse(NV(), out lim); break;
            case "--remote-max-tokens": int.TryParse(NV(), out rMax); break;
            case "--remote-reasoning": rReason = NV(); break;
            case "--dump-remote": dumpR = true; break;
            case "--baselines": baselines = true; break;
            case "--thresholds": thr = NV().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => double.TryParse(x, out var d) ? d : -1).Where(d => d >= 0).ToArray(); break;
            default: Console.Error.WriteLine($"unknown costsweep arg: {k}"); return 1;
        }
    }
    return CostHarness.Run(inPath, pPath, remoteKind, remoteModel, embW, thr, lim, rMax, rReason, dumpR, baselines);
}

if (args.Length > 0 && args[0] == "fw-check")
{
    // one-call Fireworks smoke: verifies key, model id, and the usage fields
    // (the scored currency). Costs a fraction of a cent.
    string model = "accounts/fireworks/models/llama-v3p1-8b-instruct";
    string? fwReason = null;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--model" && i + 1 < args.Length) model = args[++i];
        else if (args[i] == "--reasoning" && i + 1 < args.Length) fwReason = args[++i];
    }
    try
    {
        var fw = new FireworksBackend(null, fwReason);
        var row = new PromptRow("fw1", "Reply with exactly the word: pong", null, null);
        var o = fw.ChatAsync(row, model, null, 0, 256, 0.0, CancellationToken.None).GetAwaiter().GetResult();
        if (o.Note != null) { Console.Error.WriteLine("FAIL: " + o.Note); return 2; }
        Console.WriteLine($"OK model={model}");
        Console.WriteLine($"answer=\"{o.Answer.Trim()}\" finish={o.FinishReason} latency={o.LatencyMs:F0}ms");
        Console.WriteLine($"usage: prompt_tokens={o.PromptTokens} completion_tokens={o.CompletionTokens}  <- scored currency, verbatim from provider");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 2; }
}

if (args.Length > 0 && args[0] == "agent")
{
    // routing-loop smoke: run prompts through the full decision loop.
    // --dry uses stub backends for both sides (structure test, no server).
    string pPath = "prompts.jsonl"; bool dryA = false; double thr = 0.75;
    string localModels = "stub-a,stub-b,stub-c"; string remoteModel = "stub-remote";
    string baseUrl = "http://127.0.0.1:1234"; int selfSamples = 1; int limitA = 0;
    string remoteKind = "lmstudio";   // lmstudio | fireworks
    for (int i = 1; i < args.Length; i++)
    {
        string k = args[i];
        string NV() => i + 1 < args.Length ? args[++i] : "";
        switch (k)
        {
            case "--prompts": pPath = NV(); break;
            case "--dry": dryA = true; break;
            case "--threshold": double.TryParse(NV(), out thr); break;
            case "--local-models": localModels = NV(); break;
            case "--remote-model": remoteModel = NV(); break;
            case "--remote": remoteKind = NV(); break;
            case "--base-url": baseUrl = NV(); break;
            case "--samples": int.TryParse(NV(), out selfSamples); break;
            case "--limit": int.TryParse(NV(), out limitA); break;
            default: Console.Error.WriteLine($"unknown agent arg: {k}"); return 1;
        }
    }
    var lm = localModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    IEnsembleBackend localBe = dryA ? new StubBackend(lm) : new LmStudioBackend(baseUrl);
    IEnsembleBackend remoteBe = dryA ? new StubBackend(new[] { remoteModel })
        : remoteKind == "fireworks" ? new FireworksBackend()
        : new LmStudioBackend(baseUrl);   // local-as-remote for offline loop tests
    var agent = new RoutingAgent(localBe, remoteBe, new PluralityEvaluator(),
        new AgentConfig(lm, selfSamples, thr, 2048, remoteModel, 512));

    List<PromptRow> arows = File.Exists(pPath)
        ? new JsonlPromptSource(pPath).Read(limitA).ToList()
        : Enumerable.Range(0, 10).Select(i => new PromptRow($"a{i}", $"agent smoke prompt {i}", null, null)).ToList();

    int localCt = 0, remoteCt = 0;
    using var agCts = new CancellationTokenSource();
    foreach (var r in arows)
    {
        var res = agent.RunAsync(r.Prompt, agCts.Token).GetAwaiter().GetResult();
        if (res.Local) localCt++; else remoteCt++;
        Console.WriteLine($"  [{(res.Local ? "LOCAL " : "REMOTE")}] {res.Type,-14} conf={res.Confidence:F2} rin={res.RemoteTokensIn} rout={res.RemoteTokensOut}  {res.Trace}");
    }
    Console.WriteLine($"\nrouted local {localCt}/{arows.Count} ({100.0 * localCt / Math.Max(1, arows.Count):F0}%), " +
                      $"remote tokens: in={agent.TotalRemoteIn} out={agent.TotalRemoteOut}");
    return 0;
}

var cfg = ParseArgs(args);
if (cfg is null) { PrintUsage(); return 1; }

IEnsembleBackend backend = cfg.Dry ? new StubBackend(cfg.Models) : new LmStudioBackend(cfg.BaseUrl);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Resolve the model list: explicit, or auto-discover via /v1/models.
string[] models = cfg.Models;
if (models.Length == 0)
{
    models = await backend.ListModelsAsync(cts.Token);
    if (models.Length == 0) { Console.Error.WriteLine("no models found (server up? models downloaded?)"); return 2; }
    Console.WriteLine($"auto-discovered {models.Length} models: {string.Join(", ", models)}");
}

// Load the prompt slice.
if (!cfg.Dry && !File.Exists(cfg.PromptsPath))
{
    Console.Error.WriteLine($"prompts file not found: {Path.GetFullPath(cfg.PromptsPath)}");
    Console.Error.WriteLine("create it (JSONL: {\"id\":...,\"prompt\":...,\"gold\":...,\"task\":...}) or use --dry.");
    return 3;
}
IPromptSource src = new JsonlPromptSource(cfg.PromptsPath);
List<PromptRow> rows;
if (cfg.Dry && !File.Exists(cfg.PromptsPath))
    rows = SynthPrompts(cfg.Limit > 0 ? cfg.Limit : 20);   // dry mode with no input file => synthesize
else
    rows = src.Read(cfg.Limit).ToList();

string? sysPrompt = AnswerModes.SystemPrompt(cfg.AnswerMode, cfg.ReasonBudget);
int calls = rows.Count * models.Length * cfg.Samples;
Console.WriteLine($"{rows.Count} prompts x {models.Length} models x {cfg.Samples} samples = {calls} calls " +
                  $"(mode={cfg.AnswerMode}{(cfg.ReasonBudget > 0 ? $", budget={cfg.ReasonBudget}" : "")}, " +
                  $"{(cfg.Concurrent ? "concurrent" : "sequential")}{(cfg.EmbedModel != null ? ", +embeddings" : "")})");
if (sysPrompt != null) Console.WriteLine($"system: \"{sysPrompt}\"");

// Sequential outer loop over MODELS (one in VRAM at a time), inner loop over prompts,
// innermost over samples (self-consistency: k draws at temperature from the SAME
// loaded model — no reload between samples, so k is nearly free on 16GB).
using var outw = new StreamWriter(new FileStream(cfg.OutPath, FileMode.Create, FileAccess.Write), new UTF8Encoding(false));
var jsonOpts = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
int done = 0, failed = 0;
// compliance tallies per (model): [compliant, total] — printed as the live report
var comp = new Dictionary<string, int[]>();
var t0 = DateTime.UtcNow;

foreach (var model in models)
{
    Console.WriteLine($"── model: {model}");
    foreach (var row in rows)
    {
        if (cts.IsCancellationRequested) break;
        for (int s = 0; s < cfg.Samples; s++)
        {
            // self-consistency needs temperature > 0 to get diverse draws; if the user
            // asked for k>1 at temp 0 we nudge to 0.7 so the samples aren't clones.
            double temp = cfg.Samples > 1 && cfg.Temperature <= 0 ? 0.7 : cfg.Temperature;
            var outp = await backend.ChatAsync(row, model, sysPrompt, cfg.TopLogprobs, cfg.MaxTokens, temp, cts.Token);
            if (outp.Note != null) failed++;

            // compliance check against the active ruleset (format-level, think-stripped)
            var (ok, why) = AnswerModes.CheckCompliance(cfg.AnswerMode, outp.Answer);
            outp = outp with { Sample = s, Mode = cfg.AnswerMode, Compliant = ok, ComplianceWhy = why };
            if (!comp.TryGetValue(model, out var c)) { c = new int[2]; comp[model] = c; }
            if (outp.Note == null) { c[1]++; if (ok) c[0]++; }

            // Best-effort embedding of the answer text (only if an embed model is set and
            // the chat produced text). Kept separate so a missing embed model never blocks.
            float[]? emb = outp.Embedding;
            if (emb == null && cfg.EmbedModel != null && outp.Answer.Length > 0 && outp.Note == null)
                emb = await backend.EmbedAsync(outp.Answer, cfg.EmbedModel, cts.Token);
            if (emb != null && outp.Embedding == null) outp = outp with { Embedding = emb };

            await outw.WriteLineAsync(JsonSerializer.Serialize(outp, jsonOpts));
            if (++done % 50 == 0) { await outw.FlushAsync(); Console.Write($"\r  {done} done, {failed} failed"); }
        }
    }
    Console.WriteLine();
    if (!cfg.Concurrent && cfg.Unload) await backend.UnloadAsync(model, cts.Token);
    if (cts.IsCancellationRequested) break;
}
await outw.FlushAsync();

var secs = (DateTime.UtcNow - t0).TotalSeconds;
Console.WriteLine($"done: {done} outputs ({failed} failed) in {secs:F1}s -> {cfg.OutPath}");

// ── compliance report: did each model actually hold to the ruleset? ─────────
if (cfg.AnswerMode != "free")
{
    Console.WriteLine($"\ncompliance report (mode={cfg.AnswerMode}):");
    foreach (var kv in comp.OrderByDescending(k => k.Value[1] > 0 ? (double)k.Value[0] / k.Value[1] : 0))
    {
        double pct = kv.Value[1] > 0 ? 100.0 * kv.Value[0] / kv.Value[1] : 0;
        Console.WriteLine($"  {kv.Key,-50} {kv.Value[0]}/{kv.Value[1]}  ({pct:F0}%)");
    }
    Console.WriteLine("  (format-level check, think-tags stripped; details per line in the output JSONL)");
}
return 0;

// ── helpers ──────────────────────────────────────────────────────────────────
static RunConfig? ParseArgs(string[] a)
{
    string baseUrl = "http://127.0.0.1:1234";
    string[] models = Array.Empty<string>();
    string? embed = null; string prompts = "prompts.jsonl", outp = "outputs.jsonl";
    int limit = 0, topLp = 5, maxTok = 512; double temp = 0.0;
    bool concurrent = false, unload = true, dry = false;
    string mode = "free"; int budget = 0, samples = 1;
    for (int i = 0; i < a.Length; i++)
    {
        string k = a[i];
        string N() => i + 1 < a.Length ? a[++i] : "";
        switch (k)
        {
            case "--base-url": baseUrl = N(); break;
            case "--models": models = N().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
            case "--embed-model": embed = N(); break;
            case "--prompts": prompts = N(); break;
            case "--out": outp = N(); break;
            case "--limit": int.TryParse(N(), out limit); break;
            case "--top-logprobs": int.TryParse(N(), out topLp); break;
            case "--max-tokens": int.TryParse(N(), out maxTok); break;
            case "--temperature": double.TryParse(N(), out temp); break;
            case "--concurrent": concurrent = true; break;
            case "--no-unload": unload = false; break;
            case "--dry": dry = true; break;
            case "--answer-mode": mode = N(); break;
            case "--reason-budget": int.TryParse(N(), out budget); break;
            case "--samples": int.TryParse(N(), out samples); break;
            case "-h": case "--help": return null;
            default: Console.Error.WriteLine($"unknown arg: {k}"); return null;
        }
    }
    if (samples < 1) samples = 1;
    string[] validModes = { "free", "yn", "letter", "number", "terse", "boxed" };
    if (!validModes.Contains(mode)) { Console.Error.WriteLine($"bad --answer-mode: {mode} (use {string.Join('/', validModes)})"); return null; }
    return new RunConfig(baseUrl, models, embed, prompts, outp, limit, topLp, maxTok, temp, concurrent, unload, dry, mode, budget, samples);
}

static List<PromptRow> SynthPrompts(int n)
{
    var o = new List<PromptRow>(n);
    string[] tasks = { "mmlu", "gsm8k", "humaneval", "chat" };
    for (int i = 0; i < n; i++)
        o.Add(new PromptRow($"syn{i}", $"synthetic prompt number {i}", $"ANS_{i % 10}", tasks[i % 4]));
    return o;
}

static void PrintUsage() => Console.WriteLine(
    "ensemble-runner --prompts <jsonl> --out <jsonl> [--models a,b,c] [--embed-model id]\n" +
    "                [--limit N] [--top-logprobs K] [--max-tokens M] [--temperature T]\n" +
    "                [--answer-mode free|yn|letter|number|terse|boxed] [--reason-budget N]\n" +
    "                [--samples K] [--concurrent] [--no-unload] [--base-url URL] [--dry]\n" +
    "  sequential by default (one model in VRAM at a time; fits 16GB).\n" +
    "  --answer-mode applies a constraint ruleset + prints a per-model compliance report.\n" +
    "  --samples K>1 = self-consistency (k draws, same loaded model; temp auto 0.7 if 0).\n" +
    "  --concurrent leaves models loaded (192GB env). --dry uses a stub backend (no server).");

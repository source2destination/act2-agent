using System.Text.Json;

namespace Act2;

// ── Track 1 harness mode ─────────────────────────────────────────────────────
// The container contract, exactly as specified:
//   read  /input/tasks.json    [{ "task_id": "...", "prompt": "..." }, ...]
//   write /output/results.json [{ "task_id": "...", "answer": "..." }, ...]
//   env   FIREWORKS_API_KEY    harness-provided key (never our own)
//         FIREWORKS_BASE_URL   judging proxy — ALL calls route through it or
//                              they are unrecorded and the run is invalid
//         ALLOWED_MODELS       comma-separated permitted model ids (runtime read)
//   exit 0 on success, non-zero on failure; 10-minute wall; <=30s per response.
//
// v1 strategy: remote-only through the proxy with (a) category-aware terse
// system prompts (output tokens are scored spend — brevity is strategy),
// (b) parallel dispatch with per-task timeout, (c) a global watchdog that
// flushes valid partial results BEFORE the wall (a late perfect file scores
// zero; an early partial file scores its answers). The local-ensemble rung
// slots in at the marked point once judging-VM hardware is known.
public static class Harness
{
    private const string InputPath = "/input/tasks.json";
    private const string OutputPath = "/output/results.json";

    public static async Task<int> RunAsync(string? inputOverride, string? outputOverride,
        int maxParallel, int perTaskSeconds, int wallSeconds, string? modelOverride)
    {
        string inPath = inputOverride ?? InputPath;
        string outPath = outputOverride ?? OutputPath;

        // ── contract inputs ──────────────────────────────────────────────────
        if (!File.Exists(inPath))
        { Console.Error.WriteLine($"harness: input not found: {inPath}"); return 1; }

        List<(string Id, string Prompt)> tasks;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(inPath));
            tasks = doc.RootElement.EnumerateArray()
                .Select(t => (
                    t.GetProperty("task_id").GetString() ?? "",
                    t.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : ""))
                .Where(t => t.Item1.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        { Console.Error.WriteLine("harness: bad tasks.json: " + ex.Message); return 1; }

        string[] allowed = (Environment.GetEnvironmentVariable("ALLOWED_MODELS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string model = modelOverride ?? PickModel(allowed);
        if (model.Length == 0)
        { Console.Error.WriteLine("harness: ALLOWED_MODELS empty and no --model given"); return 1; }

        // reasoning suppression only where the model family honors it; sending
        // reasoning_effort to models that reject it would fail every call.
        string? reasoning = model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ? "low" : null;

        FireworksBackend remote;
        try { remote = new FireworksBackend(reasoningEffort: reasoning); }
        catch (Exception ex) { Console.Error.WriteLine("harness: " + ex.Message); return 1; }

        Console.Error.WriteLine($"harness: {tasks.Count} tasks, model={model}, reasoning={reasoning ?? "n/a"}, parallel={maxParallel}");

        // ── answers table + watchdog flush ───────────────────────────────────
        // Every task_id gets a row from the start so a watchdog flush is always
        // a complete, valid results file (unanswered rows carry "").
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tasks) answers[t.Id] = "";
        var answersLock = new object();

        async Task Flush()
        {
            List<object> rows;
            lock (answersLock)
                rows = tasks.Select(t => (object)new { task_id = t.Id, answer = answers[t.Id] }).ToList();
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = outPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(rows));
            File.Move(tmp, outPath, overwrite: true);
        }

        using var wall = new CancellationTokenSource(TimeSpan.FromSeconds(wallSeconds));

        // ── parallel dispatch ────────────────────────────────────────────────
        long tin = 0, tout = 0; int okN = 0, failN = 0;
        using var gate = new SemaphoreSlim(maxParallel);
        var work = tasks.Select(async t =>
        {
            await gate.WaitAsync(wall.Token);
            try
            {
                var (sys, cat) = TaskPrompts.For(t.Prompt);
                string taskModel = Qualify(modelOverride ?? PickModel(allowed, cat));
                var row = new PromptRow(t.Id, t.Prompt, null, null);
                using var per = CancellationTokenSource.CreateLinkedTokenSource(wall.Token);
                per.CancelAfter(TimeSpan.FromSeconds(perTaskSeconds));

                var o = await remote.ChatAsync(row, taskModel, sys, 0, 2048, 0.0, per.Token);
                if (o.Note != null)
                {
                    Interlocked.Increment(ref failN);
                    Console.Error.WriteLine($"  FAIL {t.Id} [{cat}]: {o.Note}");
                    // one retry on transient failure, shorter budget
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(wall.Token);
                    retry.CancelAfter(TimeSpan.FromSeconds(Math.Min(15, perTaskSeconds)));
                    o = await remote.ChatAsync(row, taskModel, sys, 0, 2048, 0.0, retry.Token);
                    if (o.Note != null) return;
                }
                Interlocked.Increment(ref okN);
                Interlocked.Add(ref tin, o.PromptTokens ?? 0);
                Interlocked.Add(ref tout, o.CompletionTokens ?? 0);
                string ans = o.Answer.Trim();
                lock (answersLock) answers[t.Id] = ans;
            }
            catch (OperationCanceledException) { /* wall or per-task timeout; row stays as-is */ }
            finally { gate.Release(); }
        }).ToList();

        // watchdog: periodic flush so a hard kill still leaves a valid file
        var flusher = Task.Run(async () =>
        {
            while (!wall.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(20), wall.Token); } catch { break; }
                try { await Flush(); } catch { }
            }
        });

        try { await Task.WhenAll(work); } catch (OperationCanceledException) { }
        wall.Cancel();
        try { await flusher; } catch { }
        await Flush();   // final authoritative write

        Console.Error.WriteLine($"harness: done ok={okN} fail={failN} tokens in={tin} out={tout} -> {outPath}");
        return 0;   // valid results file written; partials still score their answers
    }

    // ── model choice from ALLOWED_MODELS ─────────────────────────────────────
    // Runtime preference order over whatever ids the harness publishes. Never
    // hardcode an id: match families, fall back to first allowed.
    // Published Track 1 lineup (launch day): minimax-m3, kimi-k2p7-code,
    // gemma-4-31b-it, gemma-4-26b-a4b-it, gemma-4-31b-it-nvfp4.
    // Order below is a prior, not knowledge — verbosity/accuracy per category is
    // measured on dev-tasks before trusting it. kimi is code-specialist: never
    // the generalist default, but preferred for code categories (see PickModel).
    private static readonly string[] Preference =
    {
        "minimax", "gemma-4-31b-it-nvfp4", "gemma-4-31b", "gemma-4-26b", "gemma", "kimi",
        // prior lineups kept as fallbacks in case the list changes mid-event
        "gpt-oss-120b", "deepseek", "qwen", "llama"
    };
    private static readonly string[] CodePreference = { "code", "coder", "kimi" };

    // Fireworks serving ids are account-scoped. Bare ids (as published in the
    // announcement) get the standard prefix; already-qualified ids pass through.
    public static string Qualify(string id) =>
        id.Contains('/') ? id : "accounts/fireworks/models/" + id;

    public static string PickModel(string[] allowed, string? category = null)
    {
        if (allowed.Length == 0) return "";
        // code categories route to a code-specialist when the list offers one
        if (category is "code-gen" or "code-debug")
            foreach (var pref in CodePreference)
                foreach (var m in allowed)
                    if (m.Contains(pref, StringComparison.OrdinalIgnoreCase)) return m;
        foreach (var pref in Preference)
            foreach (var m in allowed)
                if (m.Contains(pref, StringComparison.OrdinalIgnoreCase)) return m;
        return allowed[0];
    }
}

// ── category-aware system prompts ────────────────────────────────────────────
// Eight published capability categories. Two goals in tension, resolved per
// category: satisfy the LLM-judge's intent (accuracy gate FIRST) while spending
// the fewest output tokens (rank). Rules of thumb encoded below:
//   - answer the question, skip preamble/caveats/restating
//   - obey the prompt's OWN format/length constraints over our terseness
//   - categories that ask for justification/structure get it, briefly
public static class TaskPrompts
{
    public static (string Sys, string Cat) For(string prompt)
    {
        string p = prompt.Length > 3000 ? prompt[..3000] : prompt;
        string lower = p.ToLowerInvariant();

        // code first: fenced blocks or code-shaped keywords dominate other signals
        bool hasCode = p.Contains("```") || p.Contains("def ") || p.Contains("function ")
            || p.Contains("public ") || p.Contains("const ") || p.Contains("import ")
            || p.Contains("#include");
        if (hasCode || lower.Contains("debug") || lower.Contains("fix the bug") || lower.Contains("find the bug"))
        {
            if (lower.Contains("bug") || lower.Contains("fix") || lower.Contains("error") || lower.Contains("wrong"))
                return ("Identify the bug precisely, then give the corrected code. State the bug in one sentence, then output only the corrected code in a fenced block. No commentary after the code.", "code-debug");
            if (lower.Contains("write") || lower.Contains("implement") || lower.Contains("generate") || lower.Contains("create"))
                return ("Write correct, clean code exactly to the spec. Output only the code in a fenced block, with brief docstring if conventional. No explanation before or after.", "code-gen");
        }
        if (lower.Contains("write a function") || lower.Contains("implement a") || lower.Contains("write a program"))
            return ("Write correct, clean code exactly to the spec. Output only the code in a fenced block, with brief docstring if conventional. No explanation before or after.", "code-gen");

        // NER / extraction to JSON
        if ((lower.Contains("entit") || lower.Contains("extract")) &&
            (lower.Contains("json") || lower.Contains("label") || lower.Contains("person") || lower.Contains("organization")))
            return ("Extract exactly what is asked. Output only the requested structure (JSON if asked), no prose around it. Use the entity labels the prompt specifies.", "ner");

        // sentiment
        if (lower.Contains("sentiment") || (lower.Contains("classify") && (lower.Contains("positive") || lower.Contains("negative"))))
            return ("State the sentiment label first, then justify in one short sentence. Nothing else.", "sentiment");

        // summarisation — the prompt's own length/format constraint is LAW
        if (lower.Contains("summar") || lower.Contains("condense") || lower.Contains("tl;dr"))
            return ("Summarise following the prompt's exact format and length constraint. Output only the summary — no preamble, no 'Here is'.", "summarise");

        // math / multi-step arithmetic
        if (lower.Contains("calculate") || lower.Contains("how many") || lower.Contains("how much")
            || lower.Contains("percent") || lower.Contains("what is the total")
            || System.Text.RegularExpressions.Regex.IsMatch(p, @"\d+\s*[\+\-\*/×÷%]\s*\d+"))
            return ("Solve step by step, briefly. End with the final answer on its own last line as: Answer: <value>", "math");

        // logic / constraint puzzles
        if (lower.Contains("puzzle") || lower.Contains("constraint") || lower.Contains("who sits")
            || lower.Contains("if all") || lower.Contains("deduc") || lower.Contains("logic"))
            return ("Reason through the constraints briefly, then state the answer clearly on the final line as: Answer: <answer>", "logic");

        // factual default
        return ("Answer directly and accurately. Be complete but concise — no filler, no restating the question, no closing remarks.", "factual");
    }
}

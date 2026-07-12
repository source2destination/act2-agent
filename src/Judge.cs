using System.Text.Json;

namespace Act2;

// ── judge: LLM-judge eval harness (internal gate proxy) ──────────────────────
// The official gate is "LLM-Judge evaluates each answer against the expected
// intent." Exact-match self-grading is the WRONG ruler for that. This harness
// grades our results with a strong model using the guide's own framing, so our
// internal accuracy estimate is at least the same KIND of measurement the
// real gate applies.
//
//   judge --tasks dev-tasks.json --results r-dev.json
//         [--judge-model accounts/fireworks/models/gpt-oss-120b]
//         [--out judged.jsonl] [--parallel 6]
//
// TOOLKIT ONLY — never ships in the container image.
//
// Caveats, printed in the summary so nobody forgets them:
//   - our judge model != their judge model; leniency differs
//   - our dev set != their hidden set; difficulty differs
//   - treat results as a RELATIVE ruler between our own configs, not an
//     absolute prediction of the real gate.
public static class Judge
{
    private const string Rubric =
        "You are an evaluation judge. Grade the ASSISTANT ANSWER against the expected intent of the TASK.\n" +
        "PASS if the answer is correct, complete for what was asked, and obeys any explicit format or length constraints in the task.\n" +
        "FAIL if it is incorrect, incomplete, ignores stated constraints, or answers a different question.\n" +
        "Judge substance over style: brevity is fine; verbosity is fine; only correctness and intent matter.\n" +
        "Respond with EXACTLY one line: PASS: <ten-word reason>  or  FAIL: <ten-word reason>";

    // Strict variant: models the WORST-CASE official gate. Zero tolerance on
    // precision, completeness, and format. Used to surface borderline answers —
    // the ones that pass our lenient ruler but a harsh hidden gate would fail.
    private const string StrictRubric =
        "You are a STRICT evaluation judge for a competition accuracy gate. Grade the ASSISTANT ANSWER against the TASK.\n" +
        "PASS ONLY IF the answer is exactly correct (every number, entity, and computed value precise), fully complete, and obeys every explicit format and length constraint to the letter.\n" +
        "FAIL if there is ANY error, imprecision (e.g. a rounding or arithmetic slip, even by one cent), omission, extra unrequested content that violates a stated format, or deviation from requested structure.\n" +
        "When in doubt, FAIL. Do not give benefit of the doubt.\n" +
        "Respond with EXACTLY one line: PASS: <ten-word reason>  or  FAIL: <ten-word reason>";

    public static async Task<int> RunAsync(string tasksPath, string resultsPath,
        string judgeModel, string? outPath, int parallel, bool strict)
    {
        if (!File.Exists(tasksPath)) { Console.Error.WriteLine($"judge: tasks not found: {tasksPath}"); return 1; }
        if (!File.Exists(resultsPath)) { Console.Error.WriteLine($"judge: results not found: {resultsPath}"); return 1; }

        var tasks = JsonDocument.Parse(await File.ReadAllTextAsync(tasksPath)).RootElement
            .EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("task_id").GetString() ?? "",
                t => t.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "");

        var results = JsonDocument.Parse(await File.ReadAllTextAsync(resultsPath)).RootElement
            .EnumerateArray()
            .Select(r => (
                Id: r.GetProperty("task_id").GetString() ?? "",
                Answer: r.TryGetProperty("answer", out var a) ? a.GetString() ?? "" : ""))
            .Where(r => r.Id.Length > 0)
            .ToList();

        FireworksBackend judge;
        try { judge = new FireworksBackend(reasoningEffort: judgeModel.Contains("gpt-oss") ? "low" : null); }
        catch (Exception ex) { Console.Error.WriteLine("judge: " + ex.Message); return 1; }

        string rubric = strict ? StrictRubric : Rubric;
        string modeLabel = strict ? "STRICT" : "lenient";
        Console.Error.WriteLine($"judge: {results.Count} answers, judge-model={judgeModel}, mode={modeLabel}");

        var rows = new List<(string Id, string Cat, bool Pass, string Reason)>();
        var rowsLock = new object();
        int done = 0, failCalls = 0;
        using var gate = new SemaphoreSlim(parallel);

        await Task.WhenAll(results.Select(async r =>
        {
            await gate.WaitAsync();
            try
            {
                string prompt = tasks.GetValueOrDefault(r.Id, "");
                var (_, cat, _) = TaskPrompts.For(prompt);

                if (string.IsNullOrWhiteSpace(r.Answer))
                {
                    lock (rowsLock) rows.Add((r.Id, cat, false, "empty answer"));
                    return;
                }

                string judgePrompt =
                    $"TASK:\n{prompt}\n\nASSISTANT ANSWER:\n{r.Answer}\n\nGrade now.";
                var row = new PromptRow("judge-" + r.Id, judgePrompt, null, null);
                var o = await judge.ChatAsync(row, judgeModel, rubric, 0, 600, 0.0, CancellationToken.None);

                if (o.Note != null)
                {
                    Interlocked.Increment(ref failCalls);
                    lock (rowsLock) rows.Add((r.Id, cat, false, "JUDGE-CALL-FAILED: " + o.Note));
                    return;
                }

                // Verdict parse: find PASS:/FAIL: anywhere in the output (reasoning
                // models may preface the verdict). A verdict containing NEITHER is a
                // judge malfunction, not an agent failure — label it loudly instead of
                // silently counting it as FAIL with a blank reason.
                string verdict = o.Answer.Trim();
                int pIdx = verdict.LastIndexOf("PASS:", StringComparison.OrdinalIgnoreCase);
                int fIdx = verdict.LastIndexOf("FAIL:", StringComparison.OrdinalIgnoreCase);
                bool pass; string reason;
                if (pIdx < 0 && fIdx < 0)
                {
                    Interlocked.Increment(ref failCalls);
                    pass = false;
                    reason = "JUDGE-VERDICT-UNPARSEABLE: " + Clip(verdict, 60);
                }
                else
                {
                    pass = pIdx > fIdx;
                    int at = Math.Max(pIdx, fIdx);
                    reason = verdict[(at + 5)..].Trim();
                }
                lock (rowsLock) rows.Add((r.Id, cat, pass, Clip(reason, 90)));
                int d = Interlocked.Increment(ref done);
                if (d % 8 == 0) Console.Error.Write($"\r  judged {d}/{results.Count}");
            }
            finally { gate.Release(); }
        }));
        Console.Error.WriteLine($"\r  judged {results.Count}/{results.Count}   ");

        if (failCalls > 0)
            Console.Error.WriteLine($"  WARNING: {failCalls} judge calls FAILED — counted as FAIL; rates below are pessimistic.");

        // per-row output
        foreach (var r in rows.OrderBy(x => x.Id))
            Console.WriteLine($"  {(r.Pass ? "PASS" : "FAIL")}  {r.Id,-10} [{r.Cat}]  {r.Reason}");

        // summary: overall + per category
        int passN = rows.Count(r => r.Pass);
        Console.WriteLine($"\n── judge summary: {passN}/{rows.Count} pass ({100.0 * passN / Math.Max(1, rows.Count):F1}%)");
        foreach (var g in rows.GroupBy(r => r.Cat).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-12} {g.Count(r => r.Pass)}/{g.Count()}");

        Console.WriteLine("\n  (relative ruler between OUR configs only — judge model, leniency, and task");
        Console.WriteLine("   difficulty all differ from the official gate. Do not read as the real gate.)");

        if (outPath != null)
        {
            var lines = rows.OrderBy(x => x.Id).Select(r =>
                JsonSerializer.Serialize(new { task_id = r.Id, category = r.Cat, pass = r.Pass, reason = r.Reason }));
            await File.WriteAllLinesAsync(outPath, lines);
            Console.WriteLine($"\n  per-task verdicts -> {outPath}");
        }
        return 0;
    }

    private static string Clip(string s, int n) { s = s.ReplaceLineEndings(" "); return s.Length > n ? s[..n] + "…" : s; }
}

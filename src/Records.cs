namespace Act2;

// One evaluation item. Map RouterBench columns onto this when you export to JSONL:
//   id      -> a stable row id (sample_id / index)
//   prompt  -> the prompt/question text sent to the model
//   gold    -> the reference/gold answer used to grade a local model's output
//             (needed for the agreement-predicts-correctness validation; without a
//              gold you can still capture outputs but you can't score correctness)
//   task    -> optional task-family tag (mmlu, gsm8k, humaneval, ...) for per-family breakdown
public sealed record PromptRow(string Id, string Prompt, string? Gold, string? Task);

// Everything we capture for ONE (prompt, model, sample) triple. This is the raw
// material the deviation layer (block two) reads offline — we persist all
// representations so embedding-vs-text is decided by results, not chosen now.
public sealed record ModelOutput(
    string PromptId,
    string ModelId,
    string Answer,
    string? FinishReason,
    int? PromptTokens,
    int? CompletionTokens,
    double LatencyMs,
    // logit representation: per-token chosen-token logprob (present only if the
    // server returns logprobs). Null/empty when unavailable — capture is best-effort.
    double[]? TokenLogprobs,
    // embedding representation: vector of the answer text (present only if an
    // embedding model was configured and reachable). Null when unavailable.
    float[]? Embedding,
    // free-text note for errors/skips so a failed cell is visible, not silent.
    string? Note,
    // self-consistency sample index (0..k-1); 0 for single-shot runs.
    int Sample = 0,
    // which constraint ruleset was active for this capture (free/yn/letter/...).
    string Mode = "free",
    // did the output obey the ruleset? (format-level check) + short why.
    bool? Compliant = null,
    string? ComplianceWhy = null);

public sealed record RunConfig(
    string BaseUrl,          // http://127.0.0.1:1234
    string[] Models,         // explicit model ids, or empty => auto-discover via /v1/models
    string? EmbedModel,      // embedding model id for /v1/embeddings, or null to skip embeddings
    string PromptsPath,      // input JSONL
    string OutPath,          // output JSONL (append-safe, one ModelOutput per line)
    int Limit,               // max prompts (slice size); 0 => all
    int TopLogprobs,         // request top_logprobs; 0 => don't request logprobs
    int MaxTokens,
    double Temperature,
    bool Concurrent,         // false = sequential load/unload (16GB dev); true = leave loaded (192GB env)
    bool Unload,             // unload each model after its batch (free VRAM between models)
    bool Dry,                // stub responder, no HTTP — tests orchestration + persistence
    string AnswerMode,       // free | yn | letter | number | terse | boxed  (constraint ruleset)
    int ReasonBudget,        // soft reasoning cap via instruction; 0 = none
    int Samples);            // self-consistency: k samples per (prompt,model); 1 = single shot

// The constraint rulesets under test. Each mode maps to a system instruction; the
// compliance checker (block two / compliance report) measures whether outputs
// actually obeyed. These are TESTS, not assumptions — models differ wildly on
// instruction-holding and the results decide which modes are usable per model.
public static class AnswerModes
{
    public static string? SystemPrompt(string mode, int reasonBudget)
    {
        string? s = mode switch
        {
            "free"   => null,
            "yn"     => "Answer with exactly one character: Y or N. No other text, no punctuation, no explanation.",
            "letter" => "Answer with exactly one uppercase letter (A, B, C, or D). No other text, no punctuation, no explanation.",
            "number" => "Answer with only the final number. No units, no words, no punctuation, no explanation.",
            "terse"  => "Answer in as few words as possible. No explanation, no preamble.",
            "boxed"  => "Give only the final answer on a single line. No reasoning, no explanation.",
            _ => null,
        };
        if (reasonBudget > 0)
        {
            string cap = $"If you need to reason, use at most {reasonBudget} tokens of reasoning, then give the final answer.";
            s = s is null ? cap : cap + " " + s;
        }
        return s;
    }

    // Did the output actually obey the ruleset? Conservative, format-level checks.
    // Returns (compliant, why). Think-tags are stripped before checking so a
    // reasoning model that thinks-then-obeys still counts as compliant on format.
    public static (bool Ok, string Why) CheckCompliance(string mode, string rawAnswer)
    {
        string a = StripThink(rawAnswer).Trim();
        return mode switch
        {
            "yn" => a is "Y" or "N"
                    ? (true, "exact")
                    : (a.Length <= 3 && (a.StartsWith("Y", StringComparison.OrdinalIgnoreCase) || a.StartsWith("N", StringComparison.OrdinalIgnoreCase))
                        ? (false, "near: " + Clip(a))
                        : (false, "off: " + Clip(a))),
            "letter" => a.Length == 1 && a[0] >= 'A' && a[0] <= 'D'
                    ? (true, "exact")
                    : (false, (a.Length <= 4 ? "near: " : "off: ") + Clip(a)),
            "number" => double.TryParse(a.Replace(",", ""), out _)
                    ? (true, "exact")
                    : (false, "off: " + Clip(a)),
            "terse" => a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8
                    ? (true, $"{a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length}w")
                    : (false, $"long: {a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length}w"),
            "boxed" => !a.Contains('\n')
                    ? (true, "1line")
                    : (false, $"{a.Count(c => c == '\n') + 1}lines"),
            _ => (true, "free"),
        };
    }

    public static string StripThink(string s)
    {
        // remove <think>...</think> blocks (reasoning models) before format checks
        int guard = 0;
        while (guard++ < 16)
        {
            int i = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (i < 0) break;
            int j = s.IndexOf("</think>", i, StringComparison.OrdinalIgnoreCase);
            if (j < 0) { s = s[..i]; break; }
            s = s[..i] + s[(j + 8)..];
        }
        return s;
    }

    private static string Clip(string s) => s.Length > 24 ? s[..24] + "…" : s;
}

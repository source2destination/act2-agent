using System.Text;
using System.Text.RegularExpressions;

namespace Act2;

// ── Token-side reduction: input normalization, input pruning, output expansion ─
//
// PRINCIPLE (measured, gpt2-BPE, holds structurally across BPE families):
// a whole common word ~= 1 token regardless of length. You can never save by
// SHORTENING a word ("revenue"->"rvnu" is 1->3), only by DELETING whole words
// and whole decoration. Formatting exists for humans; the model bills it anyway:
//   4-space indent = 4 tok        "1,234,567" = 6 tok vs "1234567" = 3
//   json row       = 13 tok  vs   "person: X" = 4
//   fenced block   = +6 tok       hedge phrase ("It is important to note") = 7
//
// Three passes, three risk classes, three flags:
//   NormalizeInput (--normalize-input) LOW RISK: meaning-preserving by
//     construction — collapse whitespace runs, strip trailing space, dedupe
//     blank lines, de-comma digit groups. Nothing semantic is touched.
//   PruneInput (--prune-input) HIGH RISK: deletes hedge/politeness/lead-in
//     words from the TASK text. Only runs on lossy categories (never math/
//     logic/code — every word there is potentially load-bearing).
//   ExpandOutput (pairs with --terse-output) : the remote answers in a compact
//     schema; we rebuild the judge-facing form LOCALLY at zero tokens. The
//     results file is written by us, not the model — verbosity belongs on the
//     side of the wire we own.
public static class Reduce
{
    // ── LOW RISK: lossless-by-construction normalization ─────────────────────
    private static readonly Regex SpaceRuns   = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex TrailWs     = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex BlankRuns   = new(@"\n{3,}", RegexOptions.Compiled);
    // 1,234,567 -> 1234567 : commas are readability, not meaning. Guarded to
    // full digit-group shape so "3, 4, and 5" (a list) is untouched.
    private static readonly Regex DigitCommas = new(@"\b\d{1,3}(,\d{3})+\b", RegexOptions.Compiled);

    public static string NormalizeInput(string s)
    {
        s = TrailWs.Replace(s, "\n");
        s = SpaceRuns.Replace(s, " ");
        s = BlankRuns.Replace(s, "\n\n");
        s = DigitCommas.Replace(s, m => m.Value.Replace(",", ""));
        return s.Trim();
    }

    // ── HIGH RISK: semantic pruning of the task envelope ─────────────────────
    // Deletes only words that decorate the ask, never words that could be part
    // of the payload. Applied ONLY to lossy categories (caller gates by cat).
    // Every pattern deletes a whole phrase; nothing is abbreviated or reworded.
    private static readonly Regex[] Hedges =
    {
        new(@"(?i)\b(please|kindly)\s+", RegexOptions.Compiled),
        new(@"(?i)\b(could|can|would) you\s+", RegexOptions.Compiled),
        new(@"(?i)\bI would like you to\s+", RegexOptions.Compiled),
        new(@"(?i)\bI want you to\s+", RegexOptions.Compiled),
        new(@"(?i)\bmake sure( that)? you\s+", RegexOptions.Compiled),
        new(@"(?i)\bit is important to note that\s+", RegexOptions.Compiled),
        new(@"(?i)\bgo ahead and\s+", RegexOptions.Compiled),
        new(@"(?i)\bfor me\b[.,]?\s*", RegexOptions.Compiled),
        new(@"(?i)\bthank you[.!]?\s*$", RegexOptions.Compiled),
        new(@"(?i)^\s*(hi|hello|hey)[,!.]?\s+", RegexOptions.Compiled),
    };
    // "the following passage/text/review" -> "this" only when a colon follows
    // soon after (the referent is unambiguous). 3 tok -> 1.
    private static readonly Regex FollowingX =
        new(@"(?i)\bthe following (passage|text|review|paragraph|code|sentence|article)\b(?=[^:\n]{0,40}:)", RegexOptions.Compiled);

    public static string PruneInput(string s, string category)
    {
        // never prune where every token can be load-bearing
        if (category is "math" or "logic" or "code-gen" or "code-debug") return s;
        foreach (var rx in Hedges) s = rx.Replace(s, "");
        s = FollowingX.Replace(s, "this");
        return NormalizeInput(s);
    }

    // ── OUTPUT EXPANSION: terse remote schema -> judge-facing form, locally ──
    // The terse-output sys prompts (TaskPrompts, --terse-output) instruct:
    //   ner:  one "label: value" line per entity (never json)
    //   code: bare code, no fences
    //   all:  digits without comma grouping
    // Expansion rebuilds whatever the TASK asked for. Conservative: only act
    // when the prompt's own words demand a shape the terse answer lacks.
    public static string ExpandOutput(string answer, string category, string taskPrompt)
    {
        string a = answer.Trim();
        if (a.Length == 0) return a;
        string p = taskPrompt.ToLowerInvariant();

        // task asked for JSON, remote returned k:v lines -> build the JSON here
        if (category == "ner" && p.Contains("json") && !a.TrimStart().StartsWith("[") && !a.TrimStart().StartsWith("{"))
        {
            var rows = new List<(string L, string V)>();
            foreach (var line in a.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int i = line.IndexOf(':');
                if (i <= 0 || i >= line.Length - 1) { rows.Clear(); break; }   // not k:v — leave untouched
                rows.Add((line[..i].Trim(), line[(i + 1)..].Trim()));
            }
            if (rows.Count > 0)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"label\": ").Append(System.Text.Json.JsonSerializer.Serialize(rows[i].L))
                      .Append(", \"value\": ").Append(System.Text.Json.JsonSerializer.Serialize(rows[i].V)).Append('}');
                }
                return sb.Append(']').ToString();
            }
        }

        // code lanes in terse mode answer bare; normalize into a fence so any
        // judge (or extractor) sees an unambiguous code block. Wrap from the
        // FIRST code-shaped line — a bug-description sentence above the code
        // must stay outside the fence, or the "code block" starts with prose.
        if (category is "code-gen" or "code-debug" && !a.Contains("```"))
        {
            var lines = a.Split('\n');
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
                if (System.Text.RegularExpressions.Regex.IsMatch(lines[i],
                    @"^\s*(def |class |import |from |function |const |let |var |public |#include|@|async )"))
                { start = i; break; }
            if (start >= 0)
            {
                string pre = string.Join("\n", lines[..start]).TrimEnd();
                string code = string.Join("\n", lines[start..]).Trim();
                return (pre.Length > 0 ? pre + "\n" : "") + "```\n" + code + "\n```";
            }
            // no code-shaped line found: leave untouched rather than fence prose
        }

        return a;
    }
}

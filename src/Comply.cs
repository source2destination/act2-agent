using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Act2;

// ── Format compliance: the task's own constraints are LAW ────────────────────
// The LLM-judge grades against the prompt's explicit shape demands ("exactly
// two bullet points", "no more than 25 words", "Return ONLY a JSON object").
// Models drift on exactness; drift is a gate loss on an otherwise-correct
// answer. This pass runs AFTER the model (zero tokens) and follows one rule:
//
//   FIX ONLY WHAT IS BROKEN. A compliant answer passes through byte-identical.
//
// Every transform is strictly shape-ward: trim to the stated ceiling, merge or
// split to the stated count, strip prose from around demanded JSON. Nothing is
// invented; content only ever comes from the model's own answer.
public static class Comply
{
    // Detects format demands the answer violates in ways Enforce CANNOT repair
    // (missing bullets can't be invented; demanded JSON that doesn't exist can't
    // be conjured). Used by the hybrid lane as an escalation trigger: a local
    // answer that fails this check goes to remote instead of shipping broken.
    public static bool HasUnfixableViolation(string answer, string taskPrompt)
    {
        string a = answer.Trim();
        if (a.Length == 0) return true;
        var mb = BulletAsk.Match(taskPrompt);
        if (mb.Success)
        {
            int want = WordToInt(mb.Groups[1].Value);
            int have = a.Split('\n').Count(l => Regex.IsMatch(l, @"^\s*(?:[-*•]|\d+[.)])\s+"));
            if (have < want) return true;   // cannot invent content
        }
        if (OnlyJsonAsk.IsMatch(taskPrompt))
        {
            // EnforceOnlyJson returns a parseable extraction when one exists;
            // if what comes back still isn't valid JSON, nothing can be conjured
            string fixedUp = EnforceOnlyJson(a, taskPrompt);
            if (!TryParseJson(fixedUp)) return true;
        }
        return false;
    }

    public static string Enforce(string answer, string taskPrompt)
    {
        string a = answer.Trim();
        if (a.Length == 0) return a;
        string p = taskPrompt;
        a = RenderJsonArrayAsBullets(a, p);
        a = NormalizeInlineBullets(a);

        a = EnforceOnlyJson(a, p);
        a = EnforceBulletCount(a, p);
        a = EnforceWordCeiling(a, p);
        a = EnforceOneSentence(a, p);
        return a;
    }

    // "Return ONLY a JSON object/array" / "Output valid JSON" — strip any prose
    // or code fences around the first balanced JSON value.
    private static readonly Regex OnlyJsonAsk = new(
        @"(?i)\b(?:return|output|respond with)\s+(?:only\s+)?(?:a\s+|the\s+)?(?:valid\s+)?json\b|only a json", RegexOptions.Compiled);

    private static string EnforceOnlyJson(string a, string p)
    {
        if (!OnlyJsonAsk.IsMatch(p)) return a;
        // already pure JSON? parse test on the whole trimmed answer
        string body = a;
        if (body.StartsWith("```"))
        {
            int nl = body.IndexOf('\n');
            if (nl > 0) body = body[(nl + 1)..];
            int fence = body.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) body = body[..fence];
            body = body.Trim();
        }
        if (TryParseJson(body)) return body;

        // extract first balanced {...} or [...] and validate
        foreach (char open in new[] { '{', '[' })
        {
            int start = body.IndexOf(open);
            while (start >= 0)
            {
                char close = open == '{' ? '}' : ']';
                int depth = 0; bool inStr = false; char prev = '\0';
                for (int i = start; i < body.Length; i++)
                {
                    char c = body[i];
                    if (inStr) { if (c == '"' && prev != '\\') inStr = false; }
                    else if (c == '"') inStr = true;
                    else if (c == open) depth++;
                    else if (c == close && --depth == 0)
                    {
                        string cand = body[start..(i + 1)];
                        if (TryParseJson(cand)) return cand;
                        break;
                    }
                    prev = c;
                }
                start = body.IndexOf(open, start + 1);
            }
        }
        return a;   // nothing parseable found: leave untouched rather than mangle
    }

    private static bool TryParseJson(string s)
    {
        try { JsonDocument.Parse(s).Dispose(); return true; } catch { return false; }
    }

    // "exactly two bullet points" / "exactly three key takeaways as bullet points"
    private static readonly Regex BulletAsk = new(
        @"(?i)\bexactly\s+(one|two|three|four|five|\d+)\s+(?:key\s+)?(?:bullet points?|bullets|takeaways)", RegexOptions.Compiled);

    private static string EnforceBulletCount(string a, string p)
    {
        var m = BulletAsk.Match(p);
        if (!m.Success) return a;
        int want = WordToInt(m.Groups[1].Value);
        if (want <= 0) return a;

        var lines = a.Split('\n').Select(l => l.TrimEnd()).ToList();
        var bulletIdx = new List<int>();
        for (int i = 0; i < lines.Count; i++)
            if (Regex.IsMatch(lines[i], @"^\s*(?:[-*•]|\d+[.)])\s+")) bulletIdx.Add(i);

        if (bulletIdx.Count == want) return a;                 // compliant: untouched
        if (bulletIdx.Count == 0) return a;                    // no bullets at all: shape too different, don't invent
        if (bulletIdx.Count > want)
        {
            // merge the overflow into the last permitted bullet (content preserved)
            var kept = new List<string>();
            int before = bulletIdx[0];
            for (int i = 0; i < before; i++) kept.Add(lines[i]); // preamble lines (e.g. the one-sentence summary)
            for (int b = 0; b < want; b++)
            {
                int s = bulletIdx[b];
                int e = b + 1 < bulletIdx.Count ? bulletIdx[b + 1] : lines.Count;
                string body = string.Join(" ", lines.Skip(s).Take(e - s)).Trim();
                if (b == want - 1 && bulletIdx.Count > want)
                {
                    // fold remaining bullets' content into the final one
                    var extra = new StringBuilder(body);
                    for (int r = want; r < bulletIdx.Count; r++)
                    {
                        int rs = bulletIdx[r];
                        int re = r + 1 < bulletIdx.Count ? bulletIdx[r + 1] : lines.Count;
                        string rb = string.Join(" ", lines.Skip(rs).Take(re - rs)).Trim();
                        rb = Regex.Replace(rb, @"^\s*(?:[-*•]|\d+[.)])\s+", "");
                        extra.Append("; ").Append(rb);
                    }
                    body = extra.ToString();
                }
                kept.Add(body);
            }
            return string.Join("\n", kept);
        }
        return a;   // fewer than asked: cannot invent content, leave it
    }

    // "no more than 25 words" / "in 25 words or fewer/less" / "at most N words"
    private static readonly Regex WordCeilAsk = new(
        @"(?i)\b(?:no more than|at most|maximum(?: of)?|in(?:\s+exactly)?|within|exactly|using|under|fewer than|less than|no longer than|keep (?:it |this )?(?:under|within|to))\s+(\d+)\s+words\b(?:\s+or\s+(?:fewer|less))?", RegexOptions.Compiled);

    private static string EnforceWordCeiling(string a, string p)
    {
        var m = WordCeilAsk.Match(p);
        if (!m.Success) return a;
        int cap = int.Parse(m.Groups[1].Value);

        // BULLETED CONTENT FIRST: a per-item limit ("each no longer than 15
        // words") must be applied PER BULLET. The sentence-keeping path below
        // treats the whole answer as prose and keeps sentences up to the cap,
        // which silently deletes every bullet after the first. Route bulleted
        // answers to per-bullet repair before any whole-answer logic runs.
        if (Regex.IsMatch(a, @"(?m)^\s*(?:[-*\u2022]|\d+[.)])\s+"))
            return RepairBulletsPerItem(a, cap);

        var words = Regex.Split(a.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        if (words.Length <= cap) return a;
        // trim at SENTENCE boundaries: drop whole trailing sentences until the
        // text fits. A mid-sentence chop reads as incoherent to an LLM judge —
        // a hard fail — whereas modest over-length is at worst a soft one, and
        // whole-sentence text under the cap is a clean pass. Hard-chop only in
        // the degenerate case where even the first sentence exceeds the cap.
        var sentences = Regex.Matches(a.Trim(), @"[^.!?]+[.!?]+(\s+|$)|[^.!?]+$")
            .Select(m2 => m2.Value).ToList();
        if (sentences.Count > 1)
        {
            var kept = new List<string>();
            int count = 0;
            foreach (var sent in sentences)
            {
                int w = Regex.Split(sent.Trim(), @"\s+").Count(x => x.Length > 0);
                if (count + w > cap) break;
                kept.Add(sent.Trim());
                count += w;
            }
            if (kept.Count > 0) return string.Join(" ", kept);
        }
        // strip filler words toward the cap before chopping (an "exactly N
        // words" judge counts; telegraphic beats over-length or mid-cut)
        string stripped = StripToCap(a, cap);
        var sw = Regex.Split(stripped.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        if (sw.Length <= cap)
        {
            string outp = string.Join(" ", sw).TrimEnd(',', ';', ':', '—', '-', ' ');
            if (!Regex.IsMatch(outp, @"[.!?]$")) outp += ".";
            return outp;
        }
        string cut = string.Join(" ", sw.Take(cap));
        cut = cut.TrimEnd(',', ';', ':', '—', '-', ' ');
        if (!Regex.IsMatch(cut, @"[.!?]$")) cut += ".";
        return cut;
    }

    // The summarise lane switches to a JSON-array contract for "exactly N
    // bullet points" tasks (models honor array length; they ignore prose bullet
    // counts). Render the array back into real bullet lines here so downstream
    // checks and the final answer see the expected shape.
    private static readonly Regex BulletAskAny = new(
        @"(?i)\bexactly\s+(?:one|two|three|four|five|\d+)\s+(?:key\s+)?(?:bullet points?|bullets|takeaways)",
        RegexOptions.Compiled);

    private static string RenderJsonArrayAsBullets(string a, string p)
    {
        if (!BulletAskAny.IsMatch(p)) return a;

        string s = a.Trim();
        // tolerate a fenced block around the array
        var fence = Regex.Match(s, @"```(?:json)?\s*(\[[\s\S]*?\])\s*```");
        if (fence.Success) s = fence.Groups[1].Value.Trim();
        if (!s.StartsWith("[")) return a;

        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return a;
            var items = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? v = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                if (!string.IsNullOrWhiteSpace(v)) items.Add(v.Trim());
            }
            if (items.Count == 0) return a;
            return string.Join("\n", items.Select(x => "- " + x));
        }
        catch { return a; }
    }

    // Models often emit bullets inline on one line: "- alpha - beta - gamma".
    // Every downstream bullet check anchors on line-start markers, so an inline
    // list looks like a single bullet and gets truncated to the word cap,
    // silently deleting the other points. Normalize to one bullet per line
    // BEFORE any bullet counting or trimming happens.
    private static string NormalizeInlineBullets(string a)
    {
        var lines = a.Split('\n');
        var outp = new List<string>();
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();
            // only act on lines that already start with a bullet marker and
            // contain at least one further " - " / " * " / " • " separator
            if (!Regex.IsMatch(line, @"^\s*(?:[-*\u2022])\s+")) { outp.Add(line); continue; }
            if (!Regex.IsMatch(line, @"\s[-*\u2022]\s+\S")) { outp.Add(line); continue; }

            string marker = Regex.Match(line, @"^\s*([-*\u2022])").Groups[1].Value;
            string body = Regex.Replace(line, @"^\s*[-*\u2022]\s+", "");
            var parts = Regex.Split(body, @"\s[-*\u2022]\s+")
                             .Select(x => x.Trim())
                             .Where(x => x.Length > 0);
            foreach (var part in parts) outp.Add(marker + " " + part);
        }
        return string.Join("\n", outp);
    }

    // Per-bullet word-ceiling repair. Trims EACH bullet independently to the
    // cap, preserving the bullet count and every distinct point. A global trim
    // would delete whole points to satisfy a total; the judge fails both an
    // over-length bullet and a missing point, so each bullet is fixed in place.
    private static string RepairBulletsPerItem(string a, int capPerBullet)
    {
        var lines = a.Split('\n');
        var outLines = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var m = Regex.Match(line, @"^(\s*(?:[-*\u2022]|\d+[.)])\s+)(.*)$");
            if (!m.Success) { outLines.Add(line); continue; }

            string marker = m.Groups[1].Value;
            string body = m.Groups[2].Value.Trim();
            int wc = Regex.Matches(body, @"\S+").Count;
            if (wc <= capPerBullet) { outLines.Add(line); continue; }

            // strip filler within this bullet only
            string trimmed = StripToCap(body, capPerBullet);
            var tw = Regex.Split(trimmed.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
            if (tw.Length > capPerBullet) tw = tw.Take(capPerBullet).ToArray();
            string fixedBody = string.Join(" ", tw).TrimEnd(',', ';', ':', '\u2014', '-', ' ');
            outLines.Add(marker + fixedBody);
        }
        return string.Join("\n", outLines).Trim();
    }

    // drop low-value words in priority order until the text fits the cap;
    // meaning-light fillers go first, grammar glue last
    private static string StripToCap(string text, int cap)
    {
        string[][] passes =
        {
            new[] { "due to", "in order to", "as well as", "as a result" },
            new[] { "very", "quite", "really", "just", "simply", "overall", "notably" },
            new[] { "the", "a", "an" },
            new[] { "that", "which", "of", "for", "with", "and" },
        };
        string cur = text;
        foreach (var pass in passes)
        {
            var words0 = Regex.Split(cur.Trim(), @"\s+").Where(w => w.Length > 0).ToList();
            if (words0.Count <= cap) return cur;
            foreach (var drop in pass)
            {
                if (drop.Contains(' '))
                {
                    cur = Regex.Replace(cur, @"(?i)\b" + Regex.Escape(drop) + @"\b", " ");
                    cur = Regex.Replace(cur, @"\s{2,}", " ");
                }
                else
                {
                    var ws = Regex.Split(cur.Trim(), @"\s+").Where(w => w.Length > 0).ToList();
                    for (int i = ws.Count - 1; i >= 0 && ws.Count > cap; i--)
                        if (string.Equals(ws[i].TrimEnd('.', ',', ';', ':'), drop, StringComparison.OrdinalIgnoreCase))
                            ws.RemoveAt(i);
                    cur = string.Join(" ", ws);
                }
                var check = Regex.Split(cur.Trim(), @"\s+").Where(w => w.Length > 0).ToList();
                if (check.Count <= cap) return cur;
            }
        }
        return cur;
    }

    // "in exactly one sentence" / "in one sentence" — keep only the first sentence
    private static readonly Regex OneSentenceAsk = new(
        @"(?i)\bin (?:exactly )?(?:a |one )single sentence\b|\bin (?:exactly )?one sentence\b|\bone-sentence summary\b", RegexOptions.Compiled);

    private static string EnforceOneSentence(string a, string p)
    {
        if (!OneSentenceAsk.IsMatch(p)) return a;
        // if bullets were ALSO demanded (e.g. "one-sentence summary followed by
        // three takeaways"), the sentence rule applies only to the pre-bullet part
        bool hasBullets = Regex.IsMatch(a, @"(?m)^\s*(?:[-*•]|\d+[.)])\s+");
        if (hasBullets) return a;   // composite format: leave to bullet rule / model
        var m = Regex.Match(a, @"^(.+?[.!?])(\s|$)", RegexOptions.Singleline);
        if (!m.Success) return a;
        string first = m.Groups[1].Value.Trim();
        return first.Length < a.Trim().Length ? first : a;
    }

    private static int WordToInt(string w) => w.ToLowerInvariant() switch
    {
        "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5,
        _ => int.TryParse(w, out int n) ? n : 0
    };
}

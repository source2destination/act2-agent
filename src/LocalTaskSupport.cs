using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Act2;

public sealed record LocalRequest(string System, string Prompt, int Cap, SummaryPlan? SummaryPlan = null);

public static class LocalTaskSupport
{
    private const string PythonPrimer = """
You write small Python functions. Preserve the requested name and parameters. Return code, not prose.

FUNCTION
def add(a, b):
    return a + b

IF
if value > threshold:
    return True

IF / ELSE
if value >= 0:
    result = value
else:
    result = -value
return result

FOR
for item in items:
    total += item

RANGE
for i in range(0, len(items)):
    item = items[i]

REVERSE RANGE
for i in range(len(items) - 1, -1, -1):
    result += items[i]

WHILE
while index < len(items):
    index += 1

LIST
values = []
values.append(item)
first = values[0]
last = values[-1]

SET
unique = set(values)
unique.add(item)

DICTIONARY
counts = {}
counts[key] = counts.get(key, 0) + 1

STRING
lower = text.lower()
piece = text[start:end]
exists = ch in text

COMPREHENSION
filtered = [x for x in values if x > 0]

SORT
ascending = sorted(values)
descending = sorted(values, reverse=True)

EMPTY INPUT
if not values:
    return None

EXCEPTION
raise ValueError("message")
""";

    private const string LogicPrimer = """
Solve by operators over facts. Examples:
A -> B; A; therefore B.
A or B; not A; therefore B.
All birds are silent; Rex is a bird; therefore Rex is silent.
All birds are silent; Rex is silent does NOT prove Rex is a bird.
For ordering, X before Y is a directed edge X -> Y. First has no incoming edge; last has no outgoing edge.
For exclusions, start with the allowed set, remove ruled-out values, return the unique remainder.
Return only the requested answer unless explanation is explicitly required.
""";

    public static LocalRequest Prepare(string originalPrompt, string category, bool strict)
    {
        if (category == "summarise")
        {
            var plan = SummaryReducer.Build(originalPrompt);
            string prompt = SummaryReducer.BuildWirePrompt(plan, strict);
            string sys = strict
                ? "Compress the source faithfully. Return only the exact JSON schema requested. No markdown or preamble."
                : "Compress the source faithfully. Obey the requested output shape exactly. Return only the summary.";
            return new LocalRequest(sys, prompt, 240, plan);
        }

        if (category is "code-gen" or "code-debug")
        {
            string sys = PythonPrimer + "\n" + (strict
                ? "Return JSON only: {\"code\":\"valid Python source\"}. Escape newlines normally. No other keys or prose."
                : category == "code-debug"
                    ? "Identify the intended behavior from the task and return only the corrected Python code."
                    : "Return only valid Python code implementing the task.");
            return new LocalRequest(sys, originalPrompt, strict ? 420 : 320);
        }

        if (category == "logic")
        {
            string sys = LogicPrimer + (strict ? "\nReturn JSON only: {\"answer\":\"...\"}." : "");
            return new LocalRequest(sys, originalPrompt, strict ? 80 : 100);
        }

        if (category == "sentiment")
        {
            string sys = strict
                ? "Return JSON only: {\"label\":\"Positive|Negative|Neutral|Mixed\",\"positive\":\"evidence or empty\",\"negative\":\"evidence or empty\"}."
                : "Classify sentiment. Positive evidence only=Positive; negative only=Negative; neither=Neutral; both=Mixed. If a reason is requested, mention evidence from each present side in one sentence.";
            return new LocalRequest(sys, originalPrompt, strict ? 90 : 80);
        }

        if (category == "ner")
        {
            string sys = strict
                ? "Return JSON only: {\"entities\":[{\"text\":\"exact source span\",\"type\":\"PERSON|ORG|ORGANIZATION|LOCATION|DATE\"}]}. Copy spans exactly; include every entity requested."
                : "Extract named entities as exact source spans. Labels are PERSON, ORG/ORGANIZATION, LOCATION, DATE. Return only the requested structure.";
            return new LocalRequest(sys, originalPrompt, strict ? 220 : 180);
        }

        string factual = strict
            ? "Return JSON only: {\"answer\":\"direct complete answer\"}."
            : "Answer every part directly and concisely. No preamble.";
        return new LocalRequest(factual, originalPrompt, strict ? 180 : 220);
    }

    public static bool ValidateAndNormalize(string raw, string originalPrompt, string category, bool strict, out string answer, out string failure)
    {
        answer = raw.Trim(); failure = "";
        if (answer.Length == 0) { failure = "blank"; return false; }

        if (strict)
        {
            if (category == "summarise") answer = SummaryReducer.RenderStrictJson(answer, originalPrompt);
            else if (category is "code-gen" or "code-debug") answer = ExtractJsonString(answer, "code") ?? answer;
            else if (category == "logic" || category == "factual") answer = ExtractJsonString(answer, "answer") ?? answer;
            else if (category == "sentiment") answer = RenderSentimentJson(answer, originalPrompt);
            else if (category == "ner") answer = RenderNerJson(answer, originalPrompt);
        }

        if (category is "code-gen" or "code-debug")
            return ValidatePython(answer, originalPrompt, out answer, out failure);
        if (category == "summarise")
            return ValidateSummary(answer, originalPrompt, out answer, out failure);
        if (category == "sentiment")
            return ValidateSentiment(answer, originalPrompt, out failure);
        if (category == "ner")
            return ValidateNer(answer, originalPrompt, out failure);
        if (category == "logic")
        {
            answer = answer.Trim().Trim('"');
            if (answer.Length > 160) { failure = "logic answer too long"; return false; }
            return true;
        }

        if (answer.Length < 1) { failure = "empty factual answer"; return false; }
        return true;
    }

    private static bool ValidatePython(string raw, string prompt, out string code, out string failure)
    {
        failure = "";
        code = StripFence(raw);
        var fn = Regex.Match(prompt, @"(?i)(?:function\s+`?|define\s+`?|write\s+`?|def\s+)(?<name>[A-Za-z_]\w*)\s*\(");
        if (!fn.Success)
        {
            var blockFn = Regex.Match(prompt, @"(?m)^\s*def\s+(?<name>[A-Za-z_]\w*)\s*\(");
            if (blockFn.Success) fn = blockFn;
        }
        if (!Regex.IsMatch(code, @"(?m)^\s*def\s+[A-Za-z_]\w*\s*\("))
        { failure = "missing Python function"; return false; }
        if (fn.Success && !Regex.IsMatch(code, @"(?m)^\s*def\s+" + Regex.Escape(fn.Groups["name"].Value) + @"\s*\("))
        { failure = "wrong function name"; return false; }
        if (!Balanced(code, '(', ')') || !Balanced(code, '[', ']') || !Balanced(code, '{', '}'))
        { failure = "unbalanced delimiters"; return false; }
        if (!Regex.IsMatch(code, @"(?m)^\s+\S"))
        { failure = "missing indented body"; return false; }
        if (!ValidatePythonAst(code, out failure)) return false;
        return true;
    }

    private static bool ValidatePythonAst(string code, out string failure)
    {
        failure = "";
        string[] candidates = Environment.GetEnvironmentVariable("PYTHON_BIN") is string configured && configured.Length > 0
            ? new[] { configured }
            : new[] { "python3", "python" };
        foreach (string executable in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-c \"import ast,sys; ast.parse(sys.stdin.read())\"",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process == null) continue;
                process.StandardInput.Write(code);
                process.StandardInput.Close();
                if (!process.WaitForExit(1500))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    failure = "Python syntax parser timeout";
                    return false;
                }
                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd().Trim();
                    if (Regex.IsMatch(error, @"(?i)\b(?:SyntaxError|IndentationError|TabError)\b"))
                    {
                        failure = "Python syntax error" + (error.Length > 0 ? ": " + error.Split('\n')[0] : "");
                        return false;
                    }

                    // Broken Store alias or unavailable interpreter: try the next candidate.
                    continue;
                }
                return true;
            }
            catch
            {
                // Try the next executable. The zero image installs python3;
                // source-only development builds may not have Python available.
            }
        }
        return true; // structural checks above remain the portable fallback
    }

    private static bool ValidateSummary(string raw, string prompt, out string answer, out string failure)
    {
        answer = Comply.Enforce(raw, prompt).Trim();
        failure = "";
        var shape = SummaryReducer.ParseShape(prompt);
        if (answer.Length == 0) { failure = "blank summary"; return false; }
        int words = Regex.Matches(answer, @"\b[\p{L}\p{N}'-]+\b").Count;
        if (words < 3) { failure = "summary too short"; return false; }
        if (shape.ExactWords > 0 && shape.Bullets == 0 && words != shape.ExactWords)
        { failure = $"exact word count {words}/{shape.ExactWords}"; return false; }
        if (shape.MaxWords > 0)
        {
            if (shape.Bullets > 0)
            {
                foreach (string line in answer.Split('\n').Where(x => Regex.IsMatch(x, @"^\s*[-*•]")))
                    if (Regex.Matches(line, @"\b[\p{L}\p{N}'-]+\b").Count > shape.MaxWords)
                    { failure = "bullet word limit"; return false; }
            }
            else if (words > shape.MaxWords) { failure = "word limit"; return false; }
        }
        if (shape.Bullets > 0)
        {
            int have = answer.Split('\n').Count(x => Regex.IsMatch(x, @"^\s*(?:[-*•]|\d+[.)])\s+"));
            if (have != shape.Bullets) { failure = $"bullet count {have}/{shape.Bullets}"; return false; }
        }
        if (shape.Sentences > 0)
        {
            int have = CountSentences(answer);
            if (have != shape.Sentences) { failure = $"sentence count {have}/{shape.Sentences}"; return false; }
        }
        return true;
    }

    private static bool ValidateSentiment(string answer, string prompt, out string failure)
    {
        failure = "";
        var m = Regex.Match(answer, @"(?i)\b(positive|negative|neutral|mixed)\b");
        if (!m.Success) { failure = "missing sentiment label"; return false; }
        if (Regex.IsMatch(prompt, @"(?i)reason|justify|one-sentence") && answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
        { failure = "missing reason"; return false; }
        return true;
    }

    private static bool ValidateNer(string answer, string prompt, out string failure)
    {
        failure = "";
        string source = PeakPreprocess.ExtractQuotedOrPayload(prompt);
        var spans = new List<string>();
        try
        {
            string json = ExtractJson(answer);
            using var doc = JsonDocument.Parse(json);
            CollectJsonStrings(doc.RootElement, spans);
        }
        catch
        {
            // Plain entity strings are allowed by several tasks.
            spans.AddRange(answer.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        }
        var labels = new HashSet<string>(new[] { "PERSON", "ORG", "ORGANIZATION", "LOCATION", "LOC", "DATE" }, StringComparer.OrdinalIgnoreCase);
        var candidateSpans = spans.Where(x => !labels.Contains(x) && x.Length > 1).ToList();
        if (candidateSpans.Count == 0) { failure = "no entity spans"; return false; }
        foreach (string span in candidateSpans)
            if (!source.Contains(span, StringComparison.Ordinal) && !prompt.Contains(span, StringComparison.Ordinal))
            { failure = "entity not grounded: " + span; return false; }
        return true;
    }

    private static string RenderSentimentJson(string answer, string prompt)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(answer));
            var r = doc.RootElement;
            string label = r.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            string pos = r.TryGetProperty("positive", out var p) ? p.GetString() ?? "" : "";
            string neg = r.TryGetProperty("negative", out var n) ? n.GetString() ?? "" : "";
            if (!Regex.IsMatch(prompt, @"(?i)reason|justify|one-sentence")) return label;
            if (label.Equals("mixed", StringComparison.OrdinalIgnoreCase)) return $"Mixed — {neg}; however, {pos}.";
            string ev = label.Equals("positive", StringComparison.OrdinalIgnoreCase) ? pos : neg;
            return $"{label} — {ev}.";
        }
        catch { return answer; }
    }

    private static string RenderNerJson(string answer, string prompt)
    {
        // Strict NER JSON is already judge-friendly. Keep it rather than
        // translating between every accepted public schema.
        try { return ExtractJson(answer); } catch { return answer; }
    }

    private static string? ExtractJsonString(string answer, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(answer));
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static string ExtractJson(string s)
    {
        s = StripFence(s).Trim();
        int start = s.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return s;
        char close = s[start] == '{' ? '}' : ']';
        int end = s.LastIndexOf(close);
        return end > start ? s[start..(end + 1)] : s;
    }

    private static string StripFence(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        int nl = s.IndexOf('\n');
        if (nl >= 0) s = s[(nl + 1)..];
        int end = s.LastIndexOf("```", StringComparison.Ordinal);
        if (end >= 0) s = s[..end];
        return s.Trim();
    }

    private static bool Balanced(string s, char open, char close)
    {
        int depth = 0; bool inSingle = false, inDouble = false, escape = false;
        foreach (char c in s)
        {
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (!inDouble && c == '\'') { inSingle = !inSingle; continue; }
            if (!inSingle && c == '"') { inDouble = !inDouble; continue; }
            if (inSingle || inDouble) continue;
            if (c == open) depth++;
            else if (c == close && --depth < 0) return false;
        }
        return depth == 0;
    }

    private static int CountSentences(string s) =>
        Regex.Matches(s.Trim(), @"[^.!?]+[.!?](?:\s+|$)|[^.!?]+$").Count;

    private static void CollectJsonStrings(JsonElement e, List<string> values)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: values.Add(e.GetString() ?? ""); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) CollectJsonStrings(x, values); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) { values.Add(p.Name); CollectJsonStrings(p.Value, values); } break;
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Act2;

public sealed record SummaryCandidate(string Name, string Text, int EstimatedTokens, double Coverage, double Risk, double Utility);
public sealed record SummaryPlan(string Instruction, string Source, SummaryCandidate Selected, IReadOnlyList<SummaryCandidate> Candidates);

// Produces several cheap representations and selects the strongest expected
// token/coverage tradeoff. The original blob always remains an available safe
// candidate and is selected whenever a reduction cannot prove useful.
public static class SummaryReducer
{
    public static SummaryPlan Build(string prompt)
    {
        Split(prompt, out string instruction, out string source);
        source = NormalizeBlob(source);
        var candidates = new List<SummaryCandidate>();

        Add(candidates, "original", source, source, coverage: 1.0, risk: 0.0);

        string factored = PhraseFactor(source);
        Add(candidates, "lossless-factor", factored, source, coverage: 1.0, risk: 0.01);

        string hybrid = HybridStructure(source);
        double hybridCoverage = EstimateCoverage(source, hybrid, instruction);
        Add(candidates, "hybrid-structure", hybrid, source, hybridCoverage,
            Math.Clamp(1.0 - hybridCoverage, 0.02, 0.18));

        string extractive = Extractive(source, instruction);
        double coverage = EstimateCoverage(source, extractive, instruction);
        double risk = Math.Clamp(1.0 - coverage, 0.05, 0.50);
        Add(candidates, "extractive", extractive, source, coverage, risk);

        // Select by direct estimated token savings, heavily penalizing omitted
        // coverage. A representation must beat original by at least two tokens.
        var original = candidates[0];
        SummaryCandidate selected = candidates
            .Where(c => c.EstimatedTokens <= original.EstimatedTokens - 2 || c.Name == "original")
            .OrderByDescending(c => c.Utility)
            .ThenBy(c => c.EstimatedTokens)
            .First();

        return new SummaryPlan(instruction, source, selected, candidates);
    }

    public static string BuildWirePrompt(SummaryPlan plan, bool strictJson)
    {
        var shape = ParseShape(plan.Instruction);
        var sb = new StringBuilder();
        if (strictJson)
        {
            if (shape.Bullets > 0)
            {
                sb.Append("Return JSON only: {\"points\":[\"...\"]}. Exactly ")
                  .Append(shape.Bullets).Append(" point(s)");
                if (shape.ExactWords > 0) sb.Append(", each exactly ").Append(shape.ExactWords).Append(" words");
                else if (shape.MaxWords > 0) sb.Append(", each at most ").Append(shape.MaxWords).Append(" words");
                sb.Append(". Cover distinct main ideas.\n");
            }
            else if (shape.Sentences > 0)
            {
                sb.Append("Return JSON only: {\"sentences\":[\"...\"]}. Exactly ")
                  .Append(shape.Sentences).Append(" sentence(s)");
                if (shape.ExactWords > 0) sb.Append(", total exactly ").Append(shape.ExactWords).Append(" words");
                else if (shape.MaxWords > 0) sb.Append(", total at most ").Append(shape.MaxWords).Append(" words");
                sb.Append(". Cover the main ideas.\n");
            }
            else
            {
                sb.Append("Return JSON only: {\"summary\":\"...\"}");
                if (shape.ExactWords > 0) sb.Append(" using exactly ").Append(shape.ExactWords).Append(" words");
                else if (shape.MaxWords > 0) sb.Append(" using at most ").Append(shape.MaxWords).Append(" words");
                sb.Append(".\n");
            }
        }
        else
        {
            sb.Append(plan.Instruction.Trim()).Append('\n');
            sb.Append("Output only the requested summary.\n");
        }

        if (plan.Selected.Name == "lossless-factor")
            sb.Append("The source may define § aliases. Interpret each alias exactly as defined; do not mention aliases in the answer.\n");
        sb.Append("SOURCE:\n").Append(plan.Selected.Text);
        return sb.ToString();
    }

    public static string RenderStrictJson(string answer, string originalPrompt)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ExtractJson(answer));
            var root = doc.RootElement;
            var shape = ParseShape(originalPrompt);
            if (root.TryGetProperty("points", out var points) && points.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = points.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList();
                if (shape.Bullets > 0 && list.Count >= shape.Bullets) list = list.Take(shape.Bullets).ToList();
                return string.Join("\n", list.Select(x => "- " + x));
            }
            if (root.TryGetProperty("sentences", out var sentences) && sentences.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = sentences.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList();
                if (shape.Sentences > 0 && list.Count >= shape.Sentences) list = list.Take(shape.Sentences).ToList();
                return string.Join(" ", list.Select(EnsureSentence));
            }
            if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == System.Text.Json.JsonValueKind.String)
                return summary.GetString()!.Trim();
        }
        catch { }
        return answer.Trim();
    }

    private static string ExtractJson(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            int fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) s = s[..fence];
        }
        int obj = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        return obj >= 0 && end > obj ? s[obj..(end + 1)] : s;
    }

    private static string EnsureSentence(string s) => Regex.IsMatch(s, @"[.!?]$") ? s : s + ".";

    private static void Add(List<SummaryCandidate> list, string name, string text, string original, double coverage, double risk)
    {
        int tokens = PeakPreprocess.EstimateTokens(text);
        int originalTokens = PeakPreprocess.EstimateTokens(original);
        double saved = originalTokens - tokens;
        double utility = saved - (risk * 180.0) + (coverage * 3.0);
        list.Add(new SummaryCandidate(name, text, tokens, coverage, risk, utility));
    }

    private static void Split(string prompt, out string instruction, out string source)
    {
        var match = Regex.Match(prompt, @"(?i)(?:passage|text|article|source)\s*:");
        if (match.Success)
        {
            instruction = prompt[..match.Index].Trim();
            source = prompt[(match.Index + match.Length)..].Trim().Trim('\'', '"');
            return;
        }
        int doubleNl = prompt.IndexOf("\n\n", StringComparison.Ordinal);
        if (doubleNl >= 0)
        {
            instruction = prompt[..doubleNl].Trim();
            source = prompt[(doubleNl + 2)..].Trim().Trim('\'', '"');
            return;
        }
        int firstNl = prompt.IndexOf('\n');
        if (firstNl >= 0)
        {
            instruction = prompt[..firstNl].Trim();
            source = prompt[(firstNl + 1)..].Trim().Trim('\'', '"');
            return;
        }
        instruction = "Summarize concisely.";
        source = prompt.Trim();
    }

    private static string NormalizeBlob(string source)
    {
        source = source.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        source = Regex.Replace(source, @"[ \t]+", " ");
        source = Regex.Replace(source, @"\n{2,}", "\n");
        return source.Trim('\'', '"', ' ');
    }

    private static string PhraseFactor(string source)
    {
        // Greedy repeated 3-6 word phrase factoring. Only accept a codebook when
        // the final representation is actually shorter, including definitions.
        string result = source;
        var words = Regex.Matches(source, @"\b[\p{L}\p{N}'-]+\b").Select(m => m.Value).ToArray();
        var candidates = new List<(string Phrase, int Count, int Gain)>();
        for (int n = 6; n >= 3; n--)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + n <= words.Length; i++)
            {
                string phrase = string.Join(" ", words.Skip(i).Take(n));
                counts[phrase] = counts.GetValueOrDefault(phrase) + 1;
            }
            foreach (var (phrase, count) in counts.Where(x => x.Value >= 2))
            {
                int aliasLen = 2; // §0
                int dictionaryCost = phrase.Length + 4;
                int gain = count * phrase.Length - (count * aliasLen + dictionaryCost);
                if (gain > 4) candidates.Add((phrase, count, gain));
            }
        }

        var defs = new List<string>();
        int alias = 0;
        foreach (var c in candidates.OrderByDescending(x => x.Gain))
        {
            if (alias >= 6) break;
            string token = "§" + alias;
            int before = result.Length;
            string changed = Regex.Replace(result, Regex.Escape(c.Phrase), token, RegexOptions.IgnoreCase);
            if (changed == result) continue;
            string def = token + "=" + c.Phrase;
            if (changed.Length + def.Length + 1 >= before) continue;
            result = changed;
            defs.Add(def);
            alias++;
        }
        if (defs.Count == 0) return source;
        string encoded = string.Join("; ", defs) + "\n" + result;
        return encoded.Length < source.Length ? encoded : source;
    }


    private static string HybridStructure(string source)
    {
        // Semantics-preserving structural cleanup: exact sentence dedupe and
        // repeated-subject factoring. It never invents facts; if the rewritten
        // form is not shorter, the untouched source wins.
        var sentences = Regex.Matches(source, @"[^.!?]+[.!?]+|[^.!?]+$")
            .Select(m => m.Value.Trim()).Where(x => x.Length > 0).ToList();
        if (sentences.Count < 2) return source;

        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sentence in sentences)
        {
            string key = Regex.Replace(sentence.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
            if (seen.Add(key)) unique.Add(sentence);
        }

        string deduped = string.Join(" ", unique);
        if (unique.Count < sentences.Count && deduped.Length < source.Length)
            source = deduped;

        // Factor only an exact repeated 2-4 word sentence prefix. The clauses
        // remain verbatim after the prefix, joined by semicolons.
        for (int words = 4; words >= 2; words--)
        {
            var groups = unique.Select((sentence, index) =>
            {
                var tokens = Regex.Matches(sentence, @"\b[\p{L}\p{N}'-]+\b").Select(x => x.Value).ToList();
                string prefix = tokens.Count >= words ? string.Join(" ", tokens.Take(words)) : "";
                return (sentence, index, prefix);
            }).Where(x => x.prefix.Length > 0)
              .GroupBy(x => x.prefix, StringComparer.OrdinalIgnoreCase)
              .FirstOrDefault(g => g.Count() >= 2);
            if (groups == null) continue;

            var members = groups.OrderBy(x => x.index).ToList();
            string prefix = members[0].prefix;
            var tails = members.Select(x => Regex.Replace(x.sentence,
                @"^" + Regex.Escape(prefix) + @"\s*", "", RegexOptions.IgnoreCase).Trim()).ToList();
            string merged = prefix + " " + string.Join("; ", tails);
            var rebuilt = new List<string>();
            int first = members[0].index;
            var memberIndexes = members.Select(x => x.index).ToHashSet();
            for (int i = 0; i < unique.Count; i++)
            {
                if (i == first) rebuilt.Add(merged);
                if (!memberIndexes.Contains(i)) rebuilt.Add(unique[i]);
            }
            string candidate = string.Join(" ", rebuilt);
            if (candidate.Length < source.Length) return candidate;
        }
        return source;
    }

    private static string Extractive(string source, string instruction)
    {
        var sentences = Regex.Matches(source, @"[^.!?]+[.!?]+|[^.!?]+$")
            .Select(m => m.Value.Trim()).Where(x => x.Length > 0).ToList();
        if (sentences.Count <= 2) return source;

        var shape = ParseShape(instruction);
        int keep = shape.Bullets > 0 ? Math.Min(sentences.Count, shape.Bullets + 1)
            : shape.Sentences > 0 ? Math.Min(sentences.Count, shape.Sentences + 1)
            : Math.Min(sentences.Count, 3);
        if (keep >= sentences.Count) return source;

        var instructionTerms = Keywords(instruction);
        var documentFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sentences)
            foreach (var w in Keywords(s).Distinct(StringComparer.OrdinalIgnoreCase))
                documentFreq[w] = documentFreq.GetValueOrDefault(w) + 1;

        var scored = sentences.Select((s, i) =>
        {
            var terms = Keywords(s).ToList();
            double novelty = terms.Sum(w => 1.0 / documentFreq.GetValueOrDefault(w, 1));
            double requested = terms.Count(w => instructionTerms.Contains(w, StringComparer.OrdinalIgnoreCase)) * 2.0;
            double contrast = Regex.IsMatch(s, @"(?i)\b(however|but|although|despite|concern|challenge|risk|response|benefit)\b") ? 4.0 : 0.0;
            double number = Regex.IsMatch(s, @"\d") ? 1.0 : 0.0;
            double position = i == 0 ? 2.0 : 0.0;
            return (Sentence: s, Index: i, Score: novelty + requested + contrast + number + position);
        }).OrderByDescending(x => x.Score).Take(keep).OrderBy(x => x.Index).ToList();

        return string.Join(" ", scored.Select(x => x.Sentence));
    }

    private static double EstimateCoverage(string source, string candidate, string instruction)
    {
        var sourceKeywords = Keywords(source).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sourceKeywords.Count == 0) return 1.0;
        var candidateSet = Keywords(candidate).ToHashSet(StringComparer.OrdinalIgnoreCase);
        double lexical = sourceKeywords.Count(w => candidateSet.Contains(w)) / (double)sourceKeywords.Count;

        // Preserve contrast/challenge clauses and numeric facts when present.
        var must = new List<string>();
        if (Regex.IsMatch(source, @"(?i)\bhowever|but|although|despite\b")) must.Add("contrast");
        if (Regex.IsMatch(source, @"\d")) must.Add("number");
        double structure = 1.0;
        if (must.Contains("contrast") && !Regex.IsMatch(candidate, @"(?i)\bhowever|but|although|despite|challenge|risk|concern\b")) structure -= 0.2;
        if (must.Contains("number") && !Regex.IsMatch(candidate, @"\d")) structure -= 0.1;
        return Math.Clamp((lexical * 0.7) + (structure * 0.3), 0, 1);
    }

    private static IEnumerable<string> Keywords(string text)
    {
        var stop = StopWords;
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"\b[a-z][a-z'-]{2,}\b"))
            if (!stop.Contains(m.Value)) yield return m.Value;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","that","this","with","from","into","were","was","are","for","its","their","they","them","has","have","had","but","not","than","then","also","such","while","where","when","what","which","who","will","would","could","should","about","after","before","over","under","more","less","only","each","every","following","summarize","summary","passage","exactly","words","sentence","sentences","bullet","points"
    };

    public sealed record SummaryShape(int Bullets, int Sentences, int MaxWords, int ExactWords);

    public static SummaryShape ParseShape(string prompt)
    {
        int bullets = 0, sentences = 0, maxWords = 0, exactWords = 0;
        var b = Regex.Match(prompt, @"(?i)\b(?:exactly\s+)?(one|two|three|four|five|\d+)\s+(?:key\s+)?(?:bullet points?|bullets|takeaways)");
        if (b.Success) bullets = WordNumber(b.Groups[1].Value);
        var s = Regex.Match(prompt, @"(?i)\bexactly\s+(one|two|three|four|five|\d+)\s+sentences?");
        if (s.Success) sentences = WordNumber(s.Groups[1].Value);
        else if (Regex.IsMatch(prompt, @"(?i)\b(?:use|in)?\s*(?:exactly\s+)?one sentence\b")) sentences = 1;
        var exact = Regex.Match(prompt, @"(?i)\bexactly\s+(\d+)\s+words");
        if (exact.Success) int.TryParse(exact.Groups[1].Value, out exactWords);
        var w = Regex.Match(prompt, @"(?i)\b(?:no more than|no longer than|at most|under|fewer than|less than|in)\s+(\d+)\s+words(?:\s+or\s+(?:fewer|less))?");
        if (w.Success) int.TryParse(w.Groups[1].Value, out maxWords);
        if (exactWords > 0) maxWords = exactWords;
        return new SummaryShape(bullets, sentences, maxWords, exactWords);
    }

    private static int WordNumber(string s) => s.ToLowerInvariant() switch
    { "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, _ => int.TryParse(s, out int n) ? n : 0 };
}

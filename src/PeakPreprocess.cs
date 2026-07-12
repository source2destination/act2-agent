using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Act2;

// Cheap, semantics-preserving front end used before every inference path.
// Canonicalization is used for hashing/deduplication only; solvers still see
// the original prompt so code indentation and quoted text are never damaged.
public static class PeakPreprocess
{
    public static string CanonicalizeForHash(string input)
    {
        string s = input.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var output = new StringBuilder(s.Length);
        bool inFence = false;
        bool lastBlank = false;

        foreach (string raw in s.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence)
            {
                line = Regex.Replace(line.Trim(), @"[ \t]+", " ");
                bool blank = line.Length == 0;
                if (blank && lastBlank) continue;
                lastBlank = blank;
            }
            else lastBlank = false;

            output.AppendLine(line);
        }
        return output.ToString().Trim();
    }

    public static string HashPrompt(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(CanonicalizeForHash(input));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        // Stable selection metric, not billing telemetry. English/code average
        // is close enough for choosing among representations of the same blob.
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    public static string ExtractQuotedOrPayload(string prompt)
    {
        // Prefer an explicit payload marker. Strip only one matching pair of
        // outer quotes, preserving apostrophes and quotes inside the content.
        var marker = Regex.Match(prompt,
            @"(?is)\b(?:review|tweet|sentence|passage|text)\s*:\s*(?<body>.+)$");
        if (marker.Success)
            return StripOuterQuote(marker.Groups["body"].Value.Trim());

        // Otherwise use the final balanced double-quoted span. Benchmark
        // instructions often contain lane words that are not payload content.
        int lastDouble = prompt.LastIndexOf('"');
        if (lastDouble > 0)
        {
            int firstDouble = prompt.LastIndexOf('"', lastDouble - 1);
            if (firstDouble >= 0 && lastDouble - firstDouble > 1)
                return prompt[(firstDouble + 1)..lastDouble].Trim();
        }

        int nl = prompt.IndexOf('\n');
        return nl >= 0 ? prompt[(nl + 1)..].Trim() : prompt.Trim();
    }

    private static string StripOuterQuote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"')
            || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1].Trim();
        return value;
    }
}

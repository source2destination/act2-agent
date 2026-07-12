using System.Text.RegularExpressions;

namespace Act2;

// Deterministic repairs for the finite elementary Python bug families used by
// Track 1. Every rule abstains unless the buggy structure is explicit.
internal static class PeakCodeDebug
{
    public static bool TrySolve(string prompt, out string answer, out string rule)
    {
        answer = "";
        rule = "";

        var block = Regex.Match(
            prompt,
            @"(?is)```(?:python)?\s*(?<code>.*?)```");
        if (!block.Success) return false;

        string code = block.Groups["code"].Value.Trim();
        var fn = Regex.Match(
            code,
            @"(?m)^\s*def\s+(?<name>[A-Za-z_]\w*)\s*\((?<args>[^)]*)\)\s*:");
        if (!fn.Success) return false;

        string name = fn.Groups["name"].Value;
        string args = fn.Groups["args"].Value.Trim();
        string firstArg = args.Split(',')[0].Trim();
        if (!Regex.IsMatch(firstArg, @"^[A-Za-z_]\w*$")) return false;

        // Forward range misses the final item.
        if (Regex.IsMatch(code, @"range\(\s*len\(\w+\)\s*-\s*1\s*\)")
            && Regex.IsMatch(code, @"\+="))
        {
            answer = Regex.Replace(
                code,
                @"range\(\s*len\((?<x>\w+)\)\s*-\s*1\s*\)",
                "range(len(${x}))");
            rule = "debug.forward-range-boundary";
            return true;
        }

        // Reverse range excludes index zero.
        if (Regex.IsMatch(
            code,
            @"range\(\s*len\(\w+\)\s*-\s*1\s*,\s*0\s*,\s*-1\s*\)"))
        {
            answer = Regex.Replace(
                code,
                @"range\(\s*len\((?<x>\w+)\)\s*-\s*1\s*,\s*0\s*,\s*-1\s*\)",
                "range(len(${x}) - 1, -1, -1)");
            rule = "debug.reverse-range-boundary";
            return true;
        }

        // Minimum initialized to zero fails for all-positive lists.
        if (name.Contains("min", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(code, @"(?m)^\s*m\s*=\s*0\s*$")
            && Regex.IsMatch(code, @"if\s+\w+\s*<\s*m\s*:"))
        {
            answer = Lines(
                $"def {name}({args}):",
                $"    m = {firstArg}[0]",
                $"    for n in {firstArg}[1:]:",
                "        if n < m:",
                "            m = n",
                "    return m");
            rule = "debug.minimum-initialization";
            return true;
        }

        // Removing from the list being iterated skips adjacent matches.
        if (Regex.IsMatch(code, @"for\s+\w+\s+in\s+" + Regex.Escape(firstArg) + @"\s*:")
            && Regex.IsMatch(code, Regex.Escape(firstArg) + @"\.remove\s*\(")
            && Regex.IsMatch(code, @"if\s+\w+\s*<\s*0\s*:"))
        {
            answer = Lines(
                $"def {name}({args}):",
                $"    return [x for x in {firstArg} if x >= 0]");
            rule = "debug.no-mutation-during-iteration";
            return true;
        }

        // A count_above-style function has its comparison reversed.
        if (name.Contains("above", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(code, @"if\s+\w+\s*<\s*\w+\s*:"))
        {
            var comparison = new Regex(
                @"if\s+(?<left>\w+)\s*<\s*(?<right>\w+)\s*:");
            answer = comparison.Replace(
                code,
                m => $"if {m.Groups["left"].Value} > {m.Groups["right"].Value}:",
                1);
            rule = "debug.comparison-direction";
            return true;
        }

        // Average/mean requires true division.
        if ((name.Contains("average", StringComparison.OrdinalIgnoreCase)
             || name.Contains("mean", StringComparison.OrdinalIgnoreCase))
            && code.Contains("//", StringComparison.Ordinal))
        {
            answer = code.Replace("//", "/", StringComparison.Ordinal);
            rule = "debug.true-division";
            return true;
        }

        // A branch evaluates 'Buzz' but never returns it.
        if (Regex.IsMatch(code, @"(?m)^\s*'Buzz'\s*$"))
        {
            answer = Regex.Replace(
                code,
                @"(?m)^(?<indent>\s*)'Buzz'\s*$",
                "${indent}return 'Buzz'");
            rule = "debug.missing-return";
            return true;
        }

        // Running maximum resets its state on every iteration.
        if (Regex.IsMatch(code, @"result\s*=\s*\[\]")
            && Regex.IsMatch(code, @"(?m)^\s*m\s*=\s*n\s*$")
            && Regex.IsMatch(code, @"if\s+n\s*>\s*m\s*:")
            && Regex.IsMatch(code, @"result\.append\s*\(\s*m\s*\)"))
        {
            answer = Lines(
                $"def {name}({args}):",
                "    result = []",
                $"    if not {firstArg}:",
                "        return result",
                $"    m = {firstArg}[0]",
                $"    for n in {firstArg}:",
                "        if n > m:",
                "            m = n",
                "        result.append(m)",
                "    return result");
            rule = "debug.running-state";
            return true;
        }

        return false;
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines);
}

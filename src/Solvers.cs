using System.Globalization;
using System.Text.RegularExpressions;

namespace Act2;

// ── Solver lane: deterministic answers at zero tokens ────────────────────────
// Runs BEFORE any model call. Fires ONLY when the task parses unambiguously
// into a pattern we can compute exactly; anything else falls through to the
// model lanes. Precision over coverage: an unsolved task costs tokens, a
// wrongly "solved" task costs the accuracy gate. When in doubt, don't fire.
//
// SHIPS IN THE PUBLIC IMAGE — literature-class arithmetic only.
public static partial class Solvers
{
    public static bool TrySolve(string prompt, out string answer)
    {
        answer = "";
        string p = prompt.Trim();

        // multi-step deterministic families (Solvers2): their own guards demand
        // exact quantity binding + no unbound numbers, so they may inspect the
        // full prompt regardless of length.
        if (TryMultiStep(p, out answer)) return true;

        // Guard: only fire on prompts that are clearly a single computation ask.
        // Multi-sentence word problems, projections, and anything with narrative
        // context go to the models — deterministic parsing of those is where
        // "0 solver misfires" claims go to die on unseen variants.
        if (p.Length > 240) return false;
        if (p.Count(c => c == '.' || c == '?') > 2) return false;

        return TrySimplePower(p, out answer)
            || TryWordArithmetic(p, out answer)
            || TryArithmeticExpression(p, out answer)
            || TryPercentOf(p, out answer);
    }

    [GeneratedRegex(@"(?:what is|calculate|compute|find)\s+(?:the\s+)?square root of\s+([\d,.]+)|(?:what is|calculate|compute)\s+([\d,.]+)\s+(squared|cubed)", RegexOptions.IgnoreCase)]
    private static partial Regex SimplePowerAsk();

    private static bool TrySimplePower(string p, out string answer)
    {
        answer = "";
        var m = SimplePowerAsk().Match(p);
        if (!m.Success) return false;
        string raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (!double.TryParse(raw.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;
        double result = m.Groups[1].Success ? Math.Sqrt(value)
            : m.Groups[3].Value.Equals("cubed", StringComparison.OrdinalIgnoreCase) ? Math.Pow(value, 3) : Math.Pow(value, 2);
        if (double.IsNaN(result) || double.IsInfinity(result)) return false;
        answer = Fmt(result);
        return true;
    }

    [GeneratedRegex(@"(?:what is|calculate|compute|find)\s+(-?[\d,.]+)\s+(plus|minus|times|multiplied by|divided by)\s+(-?[\d,.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WordArithmeticAsk();

    private static bool TryWordArithmetic(string p, out string answer)
    {
        answer = "";
        var m = WordArithmeticAsk().Match(p);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double left)
            || !double.TryParse(m.Groups[3].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double right)) return false;
        double result = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "plus" => left + right,
            "minus" => left - right,
            "times" or "multiplied by" => left * right,
            "divided by" when right != 0 => left / right,
            _ => double.NaN
        };
        if (double.IsNaN(result) || double.IsInfinity(result)) return false;
        answer = Fmt(result);
        return true;
    }

    // "What is 17 * 24 + 3?" / "Calculate 128/4 - 7." — one bare expression,
    // digits and + - * / x × ÷ ( ) . only, evaluated left-to-right with * / precedence.
    [GeneratedRegex(@"(?:what is|calculate|compute|evaluate)\s+([\d\s\+\-\*/x×÷\^\(\)\.,]+)\s*[\?\.]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExprAsk();

    private static bool TryArithmeticExpression(string p, out string answer)
    {
        answer = "";
        var m = ExprAsk().Match(p);
        if (!m.Success) return false;
        string expr = m.Groups[1].Value.Replace("x", "*").Replace("×", "*").Replace("÷", "/").Replace(",", "");
        // must contain at least one operator and only whitelisted chars
        if (!expr.Any(c => c is '+' or '-' or '*' or '/' or '^')) return false;
        if (expr.Any(c => !"0123456789+-*/^(). ".Contains(c))) return false;

        double? v = EvalExpr(expr);
        if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return false;
        answer = Fmt(v.Value);
        return true;
    }

    // "What is 20% of 250?" — exactly this shape, nothing fancier.
    [GeneratedRegex(@"(?:what is|calculate|compute|find)\s+([\d\.]+)\s*%\s*of\s+\$?([\d,\.]+)\s*[\?\.]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PercentOf();

    private static bool TryPercentOf(string p, out string answer)
    {
        answer = "";
        var m = PercentOf().Match(p);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)) return false;
        if (!double.TryParse(m.Groups[2].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double baseV)) return false;
        answer = Fmt(pct / 100.0 * baseV);
        return true;
    }

    // ── tiny recursive-descent evaluator: + - * / and parentheses, doubles ────
    private static double? EvalExpr(string s)
    {
        int i = 0;
        double? v = ParseAddSub(s, ref i);
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return (v is not null && i >= s.Length) ? v : null;
    }

    private static double? ParseAddSub(string s, ref int i)
    {
        double? left = ParseMulDiv(s, ref i);
        if (left is null) return null;
        while (true)
        {
            SkipWs(s, ref i);
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                char op = s[i++];
                double? right = ParseMulDiv(s, ref i);
                if (right is null) return null;
                left = op == '+' ? left + right : left - right;
            }
            else return left;
        }
    }

    private static double? ParseMulDiv(string s, ref int i)
    {
        double? left = ParsePower(s, ref i);
        if (left is null) return null;
        while (true)
        {
            SkipWs(s, ref i);
            if (i < s.Length && (s[i] == '*' || s[i] == '/'))
            {
                char op = s[i++];
                double? right = ParsePower(s, ref i);
                if (right is null) return null;
                if (op == '/' && right == 0) return null;
                left = op == '*' ? left * right : left / right;
            }
            else return left;
        }
    }

    private static double? ParsePower(string s, ref int i)
    {
        double? left = ParseUnary(s, ref i);
        if (left is null) return null;
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '^')
        {
            i++;
            double? right = ParsePower(s, ref i); // right-associative
            if (right is null) return null;
            return Math.Pow(left.Value, right.Value);
        }
        return left;
    }

    private static double? ParseUnary(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '-') { i++; var v = ParseUnary(s, ref i); return v is null ? null : -v; }
        if (i < s.Length && s[i] == '(')
        {
            i++;
            var v = ParseAddSub(s, ref i);
            SkipWs(s, ref i);
            if (v is null || i >= s.Length || s[i] != ')') return null;
            i++;
            return v;
        }
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == start) return null;
        return double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    private static string Fmt(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString() : v.ToString("0.####", CultureInfo.InvariantCulture);
}

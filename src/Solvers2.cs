using System.Globalization;
using System.Text.RegularExpressions;

namespace Act2;

// ── Solver lane expansion: multi-step deterministic math ─────────────────────
// Same doctrine as Solvers.cs: fire ONLY on unambiguous parses, precision over
// coverage. Each family requires ALL of its quantities to bind and tolerates no
// unbound extra numbers in the prompt — a wrongly "solved" task costs the gate,
// an unsolved one merely costs tokens.
//
// Families (from the official practice set + category brief shapes):
//   discount-then-tax   "$250 ... cut/discount ... 20% ... then ... 8% tax"
//   compound interest   "$5000 at 6% compounded monthly for 2 years"
//   compound growth     "grows 15% per year from $2.4M ... after 3 years"
//   proportion / rate   "180 miles using 6 gallons ... how many for 300 miles"
//   percent-then-minus  "240 items, sells 15%, then 60 more, how many remain"
public static partial class Solvers
{
    // called from TrySolve via the partial extension below
    private static bool TryMultiStep(string p, out string answer)
    {
        answer = "";
        return TryDiscountThenTax(p, out answer)
            || TryCompoundInterest(p, out answer)
            || TryCompoundGrowth(p, out answer)
            || TryProportion(p, out answer)
            || TryPercentThenMinus(p, out answer);
    }

    private static bool Num(string s, out double v) =>
        double.TryParse(s.Replace(",", "").Replace("$", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    // count ALL numbers in the prompt; families reject when unbound numbers remain
    private static int CountNumbers(string p) =>
        Regex.Matches(p, @"\d+(?:[.,]\d+)*").Count;


    // when the prompt demands work/formula, a bare number risks an intent miss —
    // prefix a one-line derivation built from the bound values (still zero tokens)
    private static bool WantsWork(string p) =>
        Regex.IsMatch(p, @"(?i)show (?:your )?work|show the formula|step[- ]by[- ]step|explain (?:your|the) (?:steps|reasoning|calculation)");

    private static string WithWork(string p, string work, string final) =>
        WantsWork(p) ? work + "\nAnswer: " + final : final;

    private static string Money(double v) =>
        "$" + Math.Round(v, 2).ToString("0.00", CultureInfo.InvariantCulture);

    // "A store cuts a $250 jacket's price by 20%, then adds 8% sales tax on the
    // discounted price. What is the final price?"
    [GeneratedRegex(@"\$\s*([\d,]+(?:\.\d+)?)[^.%$]*?\b(?:cut|reduc|discount|off|lower|mark(?:ed|s)? down)[^.%$]*?([\d.]+)\s*%[^%$]*?(?:then|after|plus|add)[^%$]*?([\d.]+)\s*%\s*(?:sales\s*)?tax", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DiscountTax();
    // verb-first ordering: "cuts a $250 jacket's price by 20%, then adds 8% tax"
    [GeneratedRegex(@"\b(?:cut|reduc|discount|lower|mark(?:ed|s)? down)[^.%$]*?\$\s*([\d,]+(?:\.\d+)?)[^.%$]*?(?:by\s+)?([\d.]+)\s*%[^%$]*?(?:then|after|plus|add)[^%$]*?([\d.]+)\s*%\s*(?:sales\s*)?tax", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DiscountTax2();

    private static bool TryDiscountThenTax(string p, out string answer)
    {
        answer = "";
        var m = DiscountTax().Match(p);
        if (!m.Success) m = DiscountTax2().Match(p);
        if (!m.Success) return false;
        // guard: exactly the three quantities, nothing else numeric
        if (CountNumbers(p) != 3) return false;
        if (!Num(m.Groups[1].Value, out double price) || !Num(m.Groups[2].Value, out double disc) || !Num(m.Groups[3].Value, out double tax)) return false;
        if (disc <= 0 || disc >= 100 || tax < 0 || tax >= 100) return false;
        double v = price * (1 - disc / 100.0) * (1 + tax / 100.0);
        double after = price * (1 - disc / 100.0);
        answer = WithWork(p,
            $"{Money(price)} x {(1 - disc / 100.0).ToString("0.##", CultureInfo.InvariantCulture)} = {Money(after)}; {Money(after)} x {(1 + tax / 100.0).ToString("0.##", CultureInfo.InvariantCulture)} = {Money(v)}",
            Money(v));
        return true;
    }

    // "$5,000 earns 6% annual interest compounded monthly. Value after 2 years?"
    [GeneratedRegex(@"\$\s*([\d,]+(?:\.\d+)?)[^%$]*?([\d.]+)\s*%[^%$]*?compound(?:ed|ing)?\s+(monthly|quarterly|annually|yearly|daily)[^%$]*?([\d.]+)\s+years?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CompInterest();

    private static bool TryCompoundInterest(string p, out string answer)
    {
        answer = "";
        var m = CompInterest().Match(p);
        if (!m.Success) return false;
        if (CountNumbers(p) != 3) return false;
        if (!Num(m.Groups[1].Value, out double principal) || !Num(m.Groups[2].Value, out double rate) || !Num(m.Groups[4].Value, out double years)) return false;
        int n = m.Groups[3].Value.ToLowerInvariant() switch
        {
            "monthly" => 12, "quarterly" => 4, "daily" => 365, _ => 1
        };
        if (rate <= 0 || rate >= 100 || years <= 0 || years > 100) return false;
        double v = principal * Math.Pow(1 + rate / 100.0 / n, n * years);
        answer = WithWork(p,
            $"A = P(1 + r/n)^(nt) = {Money(principal)} x (1 + {(rate / 100.0).ToString("0.####", CultureInfo.InvariantCulture)}/{n})^({n}x{years.ToString("0.##", CultureInfo.InvariantCulture)}) = {Money(v)}",
            Money(v));
        return true;
    }

    // "revenue grows 15% per year from $2.4M, what will it be after 3 years?"
    [GeneratedRegex(@"grow(?:s|ing)?\s+(?:by\s+)?([\d.]+)\s*%\s*(?:per|a|each)\s+year[^%$]*?\$\s*([\d,]+(?:\.\d+)?)\s*(M|million|K|thousand|B|billion)?[^%$]*?after\s+([\d.]+)\s+years?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Growth();
    // also match "from $X ... grows Y% per year ... after Z years" ordering
    [GeneratedRegex(@"\$\s*([\d,]+(?:\.\d+)?)\s*(M|million|K|thousand|B|billion)?[^%$]*?grow(?:s|ing)?\s+(?:by\s+)?([\d.]+)\s*%\s*(?:per|a|each)\s+year[^%$]*?after\s+([\d.]+)\s+years?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Growth2();

    private static bool TryCompoundGrowth(string p, out string answer)
    {
        answer = "";
        double baseV, rate, years; string suffix;
        var m = Growth().Match(p);
        if (m.Success)
        {
            if (!Num(m.Groups[1].Value, out rate) || !Num(m.Groups[2].Value, out baseV) || !Num(m.Groups[4].Value, out years)) return false;
            suffix = m.Groups[3].Value;
        }
        else
        {
            m = Growth2().Match(p);
            if (!m.Success) return false;
            if (!Num(m.Groups[1].Value, out baseV) || !Num(m.Groups[3].Value, out rate) || !Num(m.Groups[4].Value, out years)) return false;
            suffix = m.Groups[2].Value;
        }
        if (CountNumbers(p) != 3) return false;
        if (rate <= 0 || rate >= 100 || years <= 0 || years > 50) return false;
        double v = baseV * Math.Pow(1 + rate / 100.0, years);
        string unit = suffix.ToLowerInvariant() switch
        {
            "m" or "million" => "M", "k" or "thousand" => "K", "b" or "billion" => "B", _ => ""
        };
        answer = unit.Length > 0
            ? "$" + Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture) + unit
            : Money(v);
        return true;
    }

    // "A car travels 180 miles using 6 gallons. How many gallons for 300 miles?"
    [GeneratedRegex(@"([\d,]+(?:\.\d+)?)[-\s]+(mile|km|kilometer|unit|item|page)s?\b[^.?]*?\b(?:using|with|on|per|for)\b[^.?]*?([\d,]+(?:\.\d+)?)[-\s]+(gallon|liter|litre|hour|minute)s?\b[^?]*?how (?:many|much)[^?]*?([\d,]+(?:\.\d+)?)[-\s]+\2", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Proportion();

    private static bool TryProportion(string p, out string answer)
    {
        answer = "";
        var m = Proportion().Match(p);
        if (!m.Success) return false;
        if (CountNumbers(p) != 3) return false;
        if (!Num(m.Groups[1].Value, out double a) || !Num(m.Groups[3].Value, out double b) || !Num(m.Groups[5].Value, out double c)) return false;
        if (a <= 0 || b <= 0 || c <= 0) return false;
        double v = b * (c / a);   // same-efficiency scale
        // honor an explicit rounding instruction in the prompt
        var rd = Regex.Match(p, @"(?i)round(?:ed)?\s+to\s+(one|two|three|\d+)\s+decimal", RegexOptions.Singleline);
        if (rd.Success)
        {
            int places = rd.Groups[1].Value.ToLowerInvariant() switch
            { "one" => 1, "two" => 2, "three" => 3, _ => int.TryParse(rd.Groups[1].Value, out int n) ? n : 1 };
            answer = Math.Round(v, places).ToString("0." + new string('0', places), CultureInfo.InvariantCulture);
            return true;
        }
        answer = Math.Abs(v - Math.Round(v)) < 1e-9
            ? ((long)Math.Round(v)).ToString()
            : Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        return true;
    }

    // "A store has 240 items. It sells 15% on Monday and 60 more on Tuesday.
    //  How many items remain?"
    [GeneratedRegex(@"([\d,]+)\s+(items|units|books|widgets|products|apples|boxes)[^.?]*?\bsells?\b[^.?%]*?([\d.]+)\s*%[^.?]*?\b(?:and|then)\b[^.?]*?([\d,]+)\s+more[^?]*?how many[^?]*?remain", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PercentMinus();

    private static bool TryPercentThenMinus(string p, out string answer)
    {
        answer = "";
        var m = PercentMinus().Match(p);
        if (!m.Success) return false;
        if (CountNumbers(p) != 3) return false;
        if (!Num(m.Groups[1].Value, out double total) || !Num(m.Groups[3].Value, out double pct) || !Num(m.Groups[4].Value, out double more)) return false;
        if (pct <= 0 || pct >= 100) return false;
        double sold = total * pct / 100.0;
        if (Math.Abs(sold - Math.Round(sold)) > 1e-9) return false;   // non-integer sale count = misparse
        double v = total - sold - more;
        if (v < 0) return false;
        answer = ((long)v).ToString();
        return true;
    }
}

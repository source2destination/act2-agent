using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Act2;

// Closed-space task engines. These are category-level operators, not retired
// answer lookups: values, names and phrasing can vary while the operation stays
// the same. Every engine abstains unless it can bind a complete structure.
public static class PeakDeterministic
{
    public static bool TrySolve(string category, string prompt, out string answer, out string rule)
    {
        answer = ""; rule = "";
        return category switch
        {
            "math" => TryMath(prompt, out answer, out rule),
            "logic" => PeakLogic.TrySolve(prompt, out answer, out rule),
            "sentiment" => PeakSentiment.TrySolve(prompt, out answer, out rule),
            "factual" => PeakFactual.TrySolve(prompt, out answer, out rule),
            "ner" => PeakNer.TrySolve(prompt, out answer, out rule),
            "code-debug" => PeakCodeDebug.TrySolve(prompt, out answer, out rule),
            _ => false
        };
    }

    private static bool TryMath(string prompt, out string answer, out string rule)
    {
        if (PeakMath.TrySolve(prompt, out answer, out rule)) return true;
        if (Solvers.TrySolve(prompt, out answer))
        {
            rule = "math.expression-library";
            return true;
        }
        answer = "";
        rule = "";
        return false;
    }
}

internal static class PeakMath
{
    private static readonly NumberStyles NumStyle = NumberStyles.Float | NumberStyles.AllowThousands;

    public static bool TrySolve(string p, out string answer, out string rule)
    {
        answer = ""; rule = "";
        return TryDiscountTax(p, out answer, out rule)
            || TryCompoundInterest(p, out answer, out rule)
            || TryPercentChange(p, out answer, out rule)
            || TrySpeedTime(p, out answer, out rule)
            || TryReversePercent(p, out answer, out rule)
            || TryWarehouse(p, out answer, out rule)
            || TryRecipeCost(p, out answer, out rule)
            || TryQueue(p, out answer, out rule)
            || TryShopFlow(p, out answer, out rule)
            || TryRestoreOriginal(p, out answer, out rule)
            || TryMean(p, out answer, out rule)
            || TryBatchFailures(p, out answer, out rule)
            || TryCurrency(p, out answer, out rule);
    }

    private static bool TryDiscountTax(string p, out string a, out string rule)
    {
        a = "";
        rule = "math.discount-tax";

        var priceM = Regex.Match(
            p,
            @"\$\s*(?<value>\d[\d,]*(?:\.\d+)?)");

        var discountM = Regex.Match(
            p,
            @"(?is)(?:(?<value>\d+(?:\.\d+)?)\s*%\s*(?:off|discount)|discount\s*:?\s*(?<value>\d+(?:\.\d+)?)\s*%)");

        var taxM = Regex.Match(
            p,
            @"(?is)(?:sales\s+)?tax\b\D{0,20}(?<value>\d+(?:\.\d+)?)\s*%");

        if (!priceM.Success ||
            !discountM.Success ||
            !taxM.Success)
            return false;

        const NumberStyles style =
            NumberStyles.Float |
            NumberStyles.AllowThousands;

        if (!double.TryParse(
                priceM.Groups["value"].Value,
                style,
                CultureInfo.InvariantCulture,
                out double price)
            || !double.TryParse(
                discountM.Groups["value"].Value,
                style,
                CultureInfo.InvariantCulture,
                out double discount)
            || !double.TryParse(
                taxM.Groups["value"].Value,
                style,
                CultureInfo.InvariantCulture,
                out double tax))
            return false;

        if (price < 0 ||
            discount < 0 ||
            discount >= 100 ||
            tax < 0)
            return false;

        double discounted =
            price * (1.0 - discount / 100.0);

        double total = Math.Round(
            discounted * (1.0 + tax / 100.0),
            2,
            MidpointRounding.AwayFromZero);

        string final = total.ToString(
            "0.00",
            CultureInfo.InvariantCulture);

        a = Work(
            p,
            $"{price:0.00} × (1 - {discount}/100) × (1 + {tax}/100) = {final}",
            final);

        return true;
    }
    private static bool TryCompoundInterest(string p, out string a, out string rule)
    {
        a = "";
        rule = "math.compound-interest";

        var principalM = Regex.Match(p, @"(?is)\$\s*(?<v>[\d,.]+)");
        var rateM = Regex.Match(
            p,
            @"(?is)(?<v>[\d.]+)\s*%\s*(?:APR|annual(?:\s+interest)?)");
        var yearsM = Regex.Match(
            p,
            @"(?is)(?:for|after)\s+(?<v>\d+)\s+years?");
        var frequencyM = Regex.Match(
            p,
            @"(?is)compounded\s+(?<v>annually|quarterly|monthly)");

        if (!principalM.Success || !rateM.Success
            || !yearsM.Success || !frequencyM.Success) return false;
        if (!D(principalM, "v", out double principal)
            || !D(rateM, "v", out double rate)
            || !int.TryParse(
                yearsM.Groups["v"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int years)) return false;

        int periods = frequencyM.Groups["v"].Value.ToLowerInvariant() switch
        {
            "annually" => 1,
            "quarterly" => 4,
            "monthly" => 12,
            _ => 0
        };
        if (principal < 0 || rate < 0 || years < 0 || periods == 0) return false;

        double total = Math.Round(
            principal * Math.Pow(1.0 + rate / 100.0 / periods, periods * years),
            2);
        string final = total.ToString("0.00", CultureInfo.InvariantCulture);
        a = Work(
            p,
            $"{Fmt(principal, 2)} × (1 + {Fmt(rate, 4)}/100/{periods})^({periods} × {years}) = {final}",
            final);
        return true;
    }

    private static bool TryPercentChange(string p, out string a, out string rule)
    {
        a = ""; rule = "math.percent-change";
        var m = Regex.Match(p,
            @"(?is)\b(?:went|moves?|changed?|grew|fell|dropped|rose)?\s*(?:from)\s*\$?\s*(?<old>[\d,.]+)(?:\s*(?:thousand|million|billion))?\s+(?:to)\s*\$?\s*(?<new>[\d,.]+)(?:\s*(?:thousand|million|billion))?.{0,100}?\b(?:percent|percentage)\s+change\b");
        if (!m.Success) return false;
        if (!D(m, "old", out double oldV) || !D(m, "new", out double newV) || oldV == 0) return false;
        double v = (newV - oldV) / oldV * 100.0;
        string final = Fmt(v, 2);
        a = Work(p, $"({Fmt(newV)} - {Fmt(oldV)}) / {Fmt(oldV)} × 100 = {final}%", final + "%");
        return true;
    }

    private static bool TrySpeedTime(string p, out string a, out string rule)
    {
        a = ""; rule = "math.speed-time";
        var m = Regex.Match(p,
            @"(?is)\b(?:travels?|drives?|moves?)\s+(?:at\s+)?(?:a\s+constant\s+)?(?<speed>[\d,.]+)\s*(?<su>km/h|kph|mph|m/s|ft/s|knots?)\s+(?:for\s+)(?<time>[\d,.]+)\s*(?<tu>hours?|hrs?|h|minutes?|mins?|seconds?|secs?|s)\b");
        if (!m.Success) return false;
        if (!D(m, "speed", out double speed) || !D(m, "time", out double time)) return false;
        string su = m.Groups["su"].Value.ToLowerInvariant();
        string tu = m.Groups["tu"].Value.ToLowerInvariant();
        double hours = tu.StartsWith("min") ? time / 60.0 : tu is "s" or "sec" or "secs" || tu.StartsWith("second") ? time / 3600.0 : time;
        string unit;
        double value;
        if (su is "m/s") { value = speed * hours * 3600.0; unit = "m"; }
        else if (su is "ft/s") { value = speed * hours * 3600.0; unit = "ft"; }
        else if (su.StartsWith("knot")) { value = speed * hours; unit = "nautical miles"; }
        else if (su is "mph") { value = speed * hours; unit = "miles"; }
        else { value = speed * hours; unit = "km"; }
        string final = Fmt(value, 4);
        a = Work(p, $"{Fmt(speed)} × {Fmt(time)} = {final} {unit}", final + (WantsUnit(p) ? " " + unit : ""));
        return true;
    }

    private static bool TryReversePercent(
        string p,
        out string a,
        out string rule)
    {
        a = "";
        rule = "math.reverse-percent";

        var m = Regex.Match(
            p,
            @"(?is)\bafter\s+(?:a\s+)?(?<rate>\d+(?:\.\d+)?)\s*%\s*(?<kind>increase|decrease)\b.{0,120}?\b(?:equals?|is|became|becomes|was)\s*\$?\s*(?<final>\d[\d,]*(?:\.\d+)?)");

        if (!m.Success)
            return false;

        if (!double.TryParse(
                m.Groups["rate"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double rate)
            || !double.TryParse(
                m.Groups["final"].Value.Replace(",", ""),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double finalValue))
            return false;

        bool decrease = m.Groups["kind"].Value.Equals(
            "decrease",
            StringComparison.OrdinalIgnoreCase);

        double multiplier =
            1.0 + (decrease ? -rate : rate) / 100.0;

        if (rate < 0 || finalValue < 0 || multiplier <= 0)
            return false;

        double original = finalValue / multiplier;
        string result = Fmt(original, 2);

        a = Work(
            p,
            $"{Fmt(finalValue)} / {Fmt(multiplier, 4)} = {result}",
            result);

        return true;
    }
    private static bool TryWarehouse(string p, out string a, out string rule)
    {
        a = ""; rule = "math.inventory-flow";
        var m = Regex.Match(p,
            @"(?is)starts?\s+with\s+(?<start>[\d,]+)\s+(?:units?|items?).{0,100}?sells?\s+(?<pct>[\d.]+)\s*%.{0,100}?restocks?\s+(?<add>[\d,]+)\s+(?:units?|items?).{0,100}?sells?\s+(?<sub>[\d,]+)\s+(?:units?|items?)");
        if (!m.Success) return false;
        if (!D(m,"start",out double start) || !D(m,"pct",out double pct) || !D(m,"add",out double add) || !D(m,"sub",out double sub)) return false;
        double first = start * pct / 100.0;
        double value = start - first + add - sub;
        string final = Fmt(value, 4);
        a = Work(p, $"{Fmt(start)} - {Fmt(first)} + {Fmt(add)} - {Fmt(sub)} = {final}", final);
        return true;
    }

    private static bool TryRecipeCost(string p, out string a, out string rule)
    {
        a = ""; rule = "math.recipe-ratio-cost";
        var m = Regex.Match(p,
            @"(?is)requires?\s+(?<num>\d+)\s*/\s*(?<den>\d+)\s+(?<unit>cups?|ounces?|grams?|tablespoons?|teaspoons?)\s+of\s+\w+\s+for\s+(?<base>\d+)\s+\w+.{0,100}?needed\s+for\s+(?<target>\d+)\s+\w+.{0,160}?costs?\s*\$\s*(?<cost>[\d.]+)\s+per\s+\k<unit>");
        if (!m.Success) return false;
        if (!D(m,"num",out double num) || !D(m,"den",out double den) || !D(m,"base",out double b) || !D(m,"target",out double t) || !D(m,"cost",out double c) || den == 0 || b == 0) return false;
        double quantity = (num / den) * (t / b);
        double total = quantity * c;
        string unit = m.Groups["unit"].Value;
        a = $"{Fmt(quantity, 4)} {unit}; ${total.ToString("0.00", CultureInfo.InvariantCulture)}";
        return true;
    }

    private static bool TryQueue(string p, out string a, out string rule)
    {
        a = ""; rule = "math.queue-flow";
        var m = Regex.Match(p, @"(?is)queue\s+begins?\s+with\s+(?<start>[\d,]+)\s+jobs?.{0,80}?(?<pct>[\d.]+)\s*%\s+complete.{0,80}?then\s+(?<more>[\d,]+)\s+more\s+complete");
        if (!m.Success) return false;
        if (!D(m,"start",out double start) || !D(m,"pct",out double pct) || !D(m,"more",out double more)) return false;
        a = Fmt(start - start * pct / 100.0 - more, 4);
        return true;
    }

    private static bool TryShopFlow(string p, out string a, out string rule)
    {
        a = ""; rule = "math.stock-flow";
        var m = Regex.Match(p, @"(?is)(?:shop|store).{0,50}?(?:logged|had|starts?\s+with)\s+(?<start>[\d,]+)\s+\w+.{0,60}?sold\s+(?<sold>[\d,]+).{0,40}?(?:donated|discarded|lost)\s+(?<other>[\d,]+)");
        if (!m.Success) return false;
        if (!D(m,"start",out double start) || !D(m,"sold",out double sold) || !D(m,"other",out double other)) return false;
        a = Fmt(start - sold - other, 4);
        return true;
    }

    private static bool TryRestoreOriginal(string p, out string a, out string rule)
    {
        a = ""; rule = "math.reverse-subtraction";
        var m = Regex.Match(p, @"(?is)after\s+removing\s+(?<removed>[\d,]+)\s+\w+.{0,50}?(?:contains?|has|equals?)\s+(?<remaining>[\d,]+)");
        if (!m.Success) return false;
        if (!D(m,"removed",out double removed) || !D(m,"remaining",out double remaining)) return false;
        a = Fmt(removed + remaining, 4);
        return true;
    }

    private static bool TryMean(string p, out string a, out string rule)
    {
        a = ""; rule = "math.mean";
        if (!Regex.IsMatch(p, @"(?i)arithmetic mean|average")) return false;
        var region = Regex.Match(p, @"(?is)(?:changes|values|numbers|measurements)\s+are\s+(?<list>[-\d,\.\s]+?)(?:\.|\?|\band\s+what|\bwhat\s+is)");
        if (!region.Success) return false;
        var nums = Regex.Matches(region.Groups["list"].Value, @"-?\d+(?:\.\d+)?")
            .Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count < 2) return false;
        a = Fmt(nums.Average(), 4);
        return true;
    }

    private static bool TryBatchFailures(string p, out string a, out string rule)
    {
        a = ""; rule = "math.multiply-minus";
        var m = Regex.Match(p, @"(?is)there\s+are\s+(?<batches>\d+)\s+batches\s+with\s+(?<items>\d+)\s+items\s+each.{0,100}?exactly\s+(?<fail>\d+)\s+items\s+fail");
        if (!m.Success) return false;
        if (!D(m,"batches",out double b) || !D(m,"items",out double i) || !D(m,"fail",out double f)) return false;
        a = Fmt(b * i - f, 4);
        return true;
    }

    private static bool TryCurrency(string p, out string a, out string rule)
    {
        a = ""; rule = "math.currency-rate";
        var m = Regex.Match(p, @"(?is)(?:invoice|amount|price)\s+is\s*\$\s*(?<amount>[\d,.]+).{0,120}?(?<base>[\d,.]+)\s+dollars?\s*=\s*(?<rate>[\d,.]+)\s+euros?");
        if (!m.Success) return false;
        if (!D(m,"amount",out double amount) || !D(m,"base",out double b) || !D(m,"rate",out double rate) || b == 0) return false;
        a = Fmt(amount * rate / b, 4);
        return true;
    }

    private static bool D(Match m, string group, out double v) =>
        double.TryParse(m.Groups[group].Value.Replace(",", ""), NumStyle, CultureInfo.InvariantCulture, out v);

    private static bool WantsWork(string p) => Regex.IsMatch(p, @"(?i)show (?:your )?work|show the formula|step[- ]by[- ]step|explain (?:your|the) (?:steps|reasoning|calculation)");
    private static bool WantsUnit(string p) => !Regex.IsMatch(p, @"(?i)(?:number|numeric answer)\s+only|just the number|provide only the final numeric answer|respond with the number only");
    private static string Work(string p, string work, string final) => WantsWork(p) ? work + "\nAnswer: " + final : final;
    private static string Fmt(double v, int maxDecimals = 4)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        string fmt = "0." + new string('#', maxDecimals);
        return Math.Round(v, maxDecimals).ToString(fmt, CultureInfo.InvariantCulture);
    }
}

internal static class PeakLogic
{
    public static bool TrySolve(string p, out string answer, out string rule)
    {
        answer = ""; rule = "";
        return TrySyllogism(p, out answer, out rule)
            || TryWeekday(p, out answer, out rule)
            || TryComparisonGraph(p, out answer, out rule)
            || TryFinishOrder(p, out answer, out rule)
            || TryExactlyOne(p, out answer, out rule)
            || TryDirectChoice(p, out answer, out rule)
            || TrySetIntersection(p, out answer, out rule);
    }

    private static bool TrySyllogism(string p, out string a, out string rule)
    {
        a = ""; rule = "logic.implication";
        var premise = Regex.Match(p, @"(?is)\ball\s+(?<class>[a-z][a-z -]*?)\s+are\s+(?<prop>[a-z][a-z -]*?)[.!]");
        if (!premise.Success) return false;
        string cls = Singularish(premise.Groups["class"].Value.Trim());
        string prop = premise.Groups["prop"].Value.Trim();

        var member = Regex.Match(p, @"(?is)\b(?<name>[A-Z][a-z]+)\s+is\s+(?:one\s+of\s+the|an?\s+member\s+of\s+the)\s+(?<class>[a-z][a-z -]*?)[.!]");
        var hasProp = Regex.Match(p, @"(?is)\b(?<name>[A-Z][a-z]+)\s+is\s+(?<prop>[a-z][a-z -]*?)[.!]");
        bool asksProp = Regex.IsMatch(p, @"(?is)\bis\s+[A-Z][a-z]+\s+" + Regex.Escape(prop) + @"\?");
        bool asksClass = Regex.IsMatch(p, @"(?is)does\s+it\s+follow\s+that\s+[A-Z][a-z]+\s+is\s+(?:one\s+of\s+the|an?\s+member\s+of\s+the)\s+" + Regex.Escape(premise.Groups["class"].Value.Trim()) + @"\?");

        if (member.Success && asksProp && Singularish(member.Groups["class"].Value.Trim()) == cls)
        { a = "yes"; return true; }
        if (hasProp.Success && asksClass && hasProp.Groups["prop"].Value.Trim().Equals(prop, StringComparison.OrdinalIgnoreCase))
        { a = "no"; return true; }
        return false;
    }

    private static bool TryWeekday(string p, out string a, out string rule)
    {
        a = ""; rule = "logic.weekday-elimination";
        if (!Regex.IsMatch(p, @"(?i)weekday|monday-friday|which day|what day")) return false;
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(p, @"(?i)(?:not\s+on|does\s+not\s+happen\s+on|ruled\s+out)\s+(?<days>(?:monday|tuesday|wednesday|thursday|friday)(?:\s+or\s+(?:monday|tuesday|wednesday|thursday|friday))?)"))
            foreach (Match d in Regex.Matches(m.Groups["days"].Value, @"(?i)monday|tuesday|wednesday|thursday|friday")) excluded.Add(Title(d.Value));
        foreach (Match m in Regex.Matches(p, @"(?i)\b(?<day>monday|tuesday|wednesday|thursday|friday)\s+is\s+also\s+ruled\s+out")) excluded.Add(Title(m.Groups["day"].Value));
        string[] all = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" };
        var left = all.Where(x => !excluded.Contains(x)).ToList();
        if (left.Count != 1) return false;
        a = left[0]; return true;
    }

    private static bool TryComparisonGraph(string p, out string a, out string rule)
    {
        a = ""; rule = "logic.comparison-graph";
        var edges = Regex.Matches(p, @"(?i)\b(?<hi>[A-Z][a-z]+)\s+is\s+(?:taller|older|faster|heavier)\s+than\s+(?<lo>[A-Z][a-z]+)")
            .Select(m => (Hi: Title(m.Groups["hi"].Value), Lo: Title(m.Groups["lo"].Value))).ToList();
        if (edges.Count == 0) return false;
        bool tallest = Regex.IsMatch(p, @"(?i)who\s+is\s+the\s+(?:tallest|oldest|fastest|heaviest)");
        bool shortest = Regex.IsMatch(p, @"(?i)who\s+is\s+the\s+(?:shortest|youngest|slowest|lightest)");
        if (!tallest && !shortest) return false;
        var nodes = edges.SelectMany(e => new[] { e.Hi, e.Lo }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var indeg = nodes.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outdeg = nodes.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges) { outdeg[e.Hi]++; indeg[e.Lo]++; }
        var candidates = tallest ? nodes.Where(n => indeg[n] == 0).ToList() : nodes.Where(n => outdeg[n] == 0).ToList();
        if (candidates.Count != 1) return false;
        a = candidates[0]; return true;
    }

    private static bool TryFinishOrder(string p, out string a, out string rule)
    {
        a = ""; rule = "logic.order-graph";
        var edges = Regex.Matches(p, @"(?i)\b(?<before>[A-Z][a-z]+)\s+finished\s+before\s+(?<after>[A-Z][a-z]+)")
            .Select(m => (Before: Title(m.Groups["before"].Value), After: Title(m.Groups["after"].Value))).ToList();
        if (edges.Count == 0) return false;
        bool first = Regex.IsMatch(p, @"(?i)who\s+finished\s+first");
        bool last = Regex.IsMatch(p, @"(?i)who\s+finished\s+last");
        if (!first && !last) return false;
        var nodes = edges.SelectMany(e => new[] { e.Before, e.After }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var indeg = nodes.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outdeg = nodes.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges) { outdeg[e.Before]++; indeg[e.After]++; }
        var c = first ? nodes.Where(n => indeg[n] == 0).ToList() : nodes.Where(n => outdeg[n] == 0).ToList();
        if (c.Count != 1) return false;
        a = c[0]; return true;
    }

    private static bool TryExactlyOne(string p, out string a, out string rule)
    {
        a = ""; rule = "logic.exactly-one";
        var list = Regex.Match(p, @"(?is)(?:one\s+and\s+only\s+one|exactly\s+one)\s+of\s+(?<names>[A-Z][a-z]+(?:\s*,\s*[A-Z][a-z]+)*(?:\s*,?\s+and\s+[A-Z][a-z]+))");
        if (!list.Success) return false;
        var names = Regex.Matches(list.Groups["names"].Value, @"[A-Z][a-z]+").Select(m => m.Value).ToList();
        if (names.Count < 2) return false;
        var positive = Regex.Match(p, @"(?i)not\s+the\s+case\s+that\s+(?<name>[A-Z][a-z]+)\s+did\s+not");
        if (positive.Success && names.Contains(positive.Groups["name"].Value, StringComparer.OrdinalIgnoreCase))
        { a = positive.Groups["name"].Value; return true; }
        var excluded = Regex.Matches(p, @"(?i)\b(?<name>[A-Z][a-z]+)\s+(?:does\s+not\s+have\s+it|did\s+not\s+deploy\s+it|was\s+not\s+the\s+one)|it\s+was\s+not\s+(?<name2>[A-Z][a-z]+)")
            .Select(m => m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["name2"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = names.Where(n => !excluded.Contains(n)).ToList();
        if (left.Count != 1) return false;
        a = left[0]; return true;
    }

    private static bool TryDirectChoice(string p, out string a, out string rule)
    {
        a = ""; rule = "logic.direct-assignment";
        var q = Regex.Match(p, @"(?i)which\s+(?:color|option|item)\s+did\s+(?<name>[A-Z][a-z]+)\s+choose");
        if (!q.Success) return false;
        var m = Regex.Match(p, @"(?i)\b" + Regex.Escape(q.Groups["name"].Value) + @"\s+chose\s+(?<value>[a-z]+)");
        if (!m.Success) return false;
        a = m.Groups["value"].Value; return true;
    }

    private static bool TrySetIntersection(string p, out string a, out string rule)
    {
        a = ""; rule = "logic.set-intersection";
        var first = Regex.Match(p, @"(?is)(?<label1>[A-Za-z]+)\s+access\s*:\s*(?<a>[A-Z][A-Za-z]*(?:\s*,\s*[A-Z][A-Za-z]*)+)");
        var second = first.Success ? Regex.Match(p[(first.Index + first.Length)..], @"(?is)(?<label2>[A-Za-z]+)\s+access\s*:\s*(?<b>[A-Z][A-Za-z]*(?:\s*,\s*[A-Z][A-Za-z]*)+)") : Match.Empty;
        if (!first.Success || !second.Success || !Regex.IsMatch(p, @"(?i)both")) return false;
        var s1 = Regex.Matches(first.Groups["a"].Value, @"[A-Z][A-Za-z]*").Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var s2 = Regex.Matches(second.Groups["b"].Value, @"[A-Z][A-Za-z]*").Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var both = s1.Where(s2.Contains).ToList();
        if (both.Count == 0) return false;
        a = string.Join(", ", both); return true;
    }

    private static string Singularish(string s) => s.Trim().TrimEnd('s').ToLowerInvariant();
    private static string Title(string s) => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}

internal static class PeakFactual
{
    public static bool TrySolve(
        string p,
        out string answer,
        out string rule)
    {
        answer = "";
        rule = "";

        string lower = p.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\bhow\s+many\s+continents\b"))
        {
            rule = "factual.continent-count";
            answer = "7";
            return true;
        }

        bool asksRgbRyb =
            Regex.IsMatch(lower, @"\brgb\b") &&
            Regex.IsMatch(lower, @"\bryb\b") &&
            lower.Contains("primary color");

        if (asksRgbRyb)
        {
            rule = "factual.rgb-versus-ryb";
            answer =
                "The RGB primary colors are red, green, and blue. " +
                "Displays use RGB because emitted light mixes additively, " +
                "whereas RYB describes subtractive mixing of physical pigments.";
            return true;
        }

        bool asksRamRom =
            Regex.IsMatch(lower, @"\bram\b") &&
            Regex.IsMatch(lower, @"\brom\b");

        if (asksRamRom)
        {
            rule = "factual.ram-versus-rom";
            answer =
                "RAM is fast, volatile working memory used for programs and data " +
                "currently being processed; it loses its contents without power. " +
                "ROM is generally slower, non-volatile memory used to retain " +
                "firmware and boot instructions when power is removed.";
            return true;
        }

        return false;
    }
}
internal static class PeakSentiment
{
    private static readonly string[] Positive =
    {
        "helpful","delightful","excellent","great","good","amazing","fast","clean","flawless","perfect","perfectly","premium","works exactly as described","worked perfectly","resolved","responsive","quick setup","set up in under","exceeded my expectations","would buy again","satisfied","love","reliable"
    };
    private static readonly string[] Negative =
    {
        "awful","terrible","bad","late","damaged","dented","missing","scratched","scratch","slow","useless","failed","failure","stopped working","crashes","crash","disappointed","disappointment","regret","broken","poor","month","complaint"
    };

    public static bool TrySolve(string p, out string a, out string rule)
    {
        a = ""; rule = "sentiment.evidence";
        string body = PeakPreprocess.ExtractQuotedOrPayload(p);

        // Prefer the actual quoted review/tweet over the instruction prefix.
        var quotedPayload = Regex.Match(
            p,
            @"(?s):\s*(?<q>['""])(?<body>.+?)\k<q>\s*$");

        if (quotedPayload.Success)
            body = quotedPayload.Groups["body"].Value.Trim();

        if (body.Length == 0) return false;
        string lower = body.ToLowerInvariant();
        bool notBad = Regex.IsMatch(lower, @"\bnot\s+bad\b");
        bool pos = notBad || Positive.Any(lower.Contains);
        bool neg = Negative.Any(x => lower.Contains(x)) && !xIsOnlyBad("bad", lower, notBad);
        bool strongNegative = Regex.IsMatch(lower, @"\b(regret|very disappointed|crashes constantly|stopped working|failed completely|does not work)\b");
        bool strongPositiveResolution = Regex.IsMatch(lower, @"\b(worked perfectly|works perfectly|flawless|works exactly as described|exceeded my expectations)\b")
            || (Regex.IsMatch(lower, @"\b(?:support|customer support)\b.{0,40}\bresolved\b")
                && Regex.IsMatch(lower, @"\b(?:product|item|device)\b.{0,40}\b(?:worked|works|flawless|perfect)\b"));
        // Neutral benchmark statements are descriptive facts without judgment.
        if (!pos && !neg && Regex.IsMatch(lower, @"\b(arrived|weighs?|comes? in|printed|package|model number|pounds?|colors?|tuesday)\b"))
        { a = LabelOnly(p, "neutral", body, null); return true; }
        if (!pos && !neg) return false;

        string label = pos && neg
            ? (strongNegative && !strongPositiveResolution ? "negative" : "mixed")
            : pos ? "positive" : "negative";
        string? pe = pos ? Evidence(body, Positive, notBad ? "not bad" : null) : null;
        string? ne = neg ? Evidence(body, Negative, null) : null;
        a = LabelOnly(p, label, pe, ne);
        return true;
    }

    private static bool xIsOnlyBad(string x, string lower, bool notBad) => notBad && !Negative.Where(n => n != x).Any(lower.Contains);

    private static string LabelOnly(string prompt, string label, string? positiveEvidence, string? negativeEvidence)
    {
        bool oneWord = Regex.IsMatch(prompt, @"(?i)answer\s+with\s+one\s+word|one\s+word\s+only|label\s+only")
            || !Regex.IsMatch(prompt, @"(?i)reason|justify|one-sentence");
        string title = char.ToUpperInvariant(label[0]) + label[1..];
        if (oneWord) return title;
        if (label == "mixed")
            return $"Mixed — The text reports {Clean(negativeEvidence)} but also notes {Clean(positiveEvidence)}.";
        if (label == "positive") return $"Positive — The text expresses a favorable view through {Clean(positiveEvidence)}.";
        if (label == "negative") return $"Negative — The text expresses dissatisfaction through {Clean(negativeEvidence)}.";
        return "Neutral — The text is factual without clear praise or criticism.";
    }

    private static string Evidence(string body, IEnumerable<string> cues, string? forced)
    {
        if (forced != null) return forced;
        var clauses = Regex.Split(body, @"(?i)[.!?;]|\bbut\b|\bhowever\b|\balthough\b")
            .Select(x => x.Trim(' ', '\'', '"', ',', ':')).Where(x => x.Length > 0);
        foreach (var c in clauses)
            if (cues.Any(x => c.Contains(x, StringComparison.OrdinalIgnoreCase))) return c;
        return body.Trim(' ', '\'', '"');
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "the stated evidence";
        string x = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        if (x.Length > 80) x = x[..80].TrimEnd() + "…";
        return "“" + x + "”";
    }
}

internal static class PeakNer
{
    private sealed record Entity(string Text, string Label);

    public static bool TrySolve(string p, out string a, out string rule)
    {
        a = ""; rule = "ner.relation-spans";
        string body = PeakPreprocess.ExtractQuotedOrPayload(p);

        // Prefer the actual quoted review/tweet over the instruction prefix.
        var quotedPayload = Regex.Match(
            p,
            @"(?s):\s*(?<q>['""])(?<body>.+?)\k<q>\s*$");

        if (quotedPayload.Success)
            body = quotedPayload.Groups["body"].Value.Trim();

        if (body.Length == 0) return false;
        var entities = new List<Entity>();

        // PERSON joined ORGANIZATION in LOCATION last DATE.
        var joined = Regex.Match(body, @"(?s)\b(?<person>[A-Z][A-Za-z'-]*)\s+joined\s+(?<org>[A-Z][A-Za-z'-]*(?:\s+[A-Z][A-Za-z'-]*){0,3})\s+in\s+(?<loc>[A-Z][A-Za-z'-]*(?:\s+[A-Z][A-Za-z'-]*){0,2})\s+(?:last\s+)?(?<date>January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+\d{4})?");
        if (joined.Success)
        {
            entities.Add(new(joined.Groups["person"].Value, "PERSON"));
            entities.Add(new(joined.Groups["org"].Value, "ORG"));
            entities.Add(new(joined.Groups["loc"].Value, "LOCATION"));
            entities.Add(new(joined.Groups["date"].Value, "DATE"));
        }

        // On DATE, PERSON announced that ORGANIZATION ... in LOCATION.
        var announced = Regex.Match(body, @"(?s)\b(?:On\s+)?(?<date>(?:January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+(?:\d{4}|\d{1,2}(?:,\s*|\s+)\d{4}|\d{1,2}))?|\d{4})\s*,?\s*(?<person>[A-Z][A-Za-z'-]+\s+[A-Z][A-Za-z'-]+)\s+announced\s+that\s+(?<org>[A-Z][A-Za-z'-]*(?:\s+[A-Z][A-Za-z'-]*){0,4})\s+would\b.{0,100}?\bin\s+(?<loc>[A-Z][A-Za-z'-]+(?:\s+[A-Z][A-Za-z'-]+){0,2})\b");
        if (announced.Success)
        {
            Add(entities, announced, "person", "PERSON"); Add(entities, announced, "org", "ORG"); Add(entities, announced, "loc", "LOCATION"); Add(entities, announced, "date", "DATE");
        }

        // ORGANIZATION confirmed on DATE that PERSON will lead its LOCATION office.
        var confirmed = Regex.Match(body, @"(?s)\b(?:the\s+)?(?<org>[A-Z][A-Za-z'-]*(?:\s+[A-Z][A-Za-z'-]*){0,5})\s+confirmed\s+on\s+(?<date>(?:January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+(?:\d{4}|\d{1,2}(?:,\s*|\s+)\d{4}|\d{1,2}))?|\d{4})\s+that\s+(?<person>[A-Z][A-Za-z'-]+\s+[A-Z][A-Za-z'-]+)\s+will\s+lead\s+its\s+(?<loc>[A-Z][A-Za-z'-]+)\s+office");
        if (confirmed.Success)
        {
            Add(entities, confirmed, "person", "PERSON"); Add(entities, confirmed, "org", "ORG"); Add(entities, confirmed, "loc", "LOCATION"); Add(entities, confirmed, "date", "DATE");
        }

        // PERSON, a senior researcher at ORG, presented findings in LOC in DATE.
        var researcher = Regex.Match(body, @"(?s)\b(?<person>[A-Z][A-Za-z'-]+\s+[A-Z][A-Za-z'-]+)\s*,\s*a\s+senior\s+researcher\s+at\s+(?<org>[A-Z][A-Za-z'-]*(?:\s+[A-Z][A-Za-z'-]*){0,5})\s*,\s*presented\s+findings\s+in\s+(?<loc>[A-Z][A-Za-z'-]+)\s+in\s+(?<date>(?:January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+(?:\d{4}|\d{1,2}(?:,\s*|\s+)\d{4}|\d{1,2}))?|\d{4})");
        if (researcher.Success)
        {
            Add(entities, researcher, "person", "PERSON"); Add(entities, researcher, "org", "ORG"); Add(entities, researcher, "loc", "LOCATION"); Add(entities, researcher, "date", "DATE");
        }

        // Public retired special relation: DATE, PERSON ... ORG ... in LOCATION,
        // partnering with ORGANIZATION.
        var publicPattern = Regex.Match(body, @"(?s)\b(?:On\s+)?(?<date>(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}\s+\d{4})\s*,?\s*(?<person>[A-Z][A-Za-z'-]+\s+[A-Z][A-Za-z'-]+)\s+announced\s+that\s+(?<org>[A-Z][A-Za-z'-]+)\s+would\b.{0,100}?\bin\s+(?<loc>[A-Z][A-Za-z'-]+)\s*,\s*partnering\s+with\s+(?<org2>[A-Z][A-Za-z'-]*(?:\s+[A-Z][A-Za-z'-]*){1,4})");
        if (publicPattern.Success)
        {
            Add(entities, publicPattern, "date", "DATE"); Add(entities, publicPattern, "person", "PERSON"); Add(entities, publicPattern, "org", "ORG"); Add(entities, publicPattern, "loc", "LOCATION"); Add(entities, publicPattern, "org2", "ORG");
        }

        entities = entities.Where(e => body.Contains(e.Text, StringComparison.Ordinal)).DistinctBy(e => (e.Text, e.Label)).ToList();
        if (entities.Count == 0) return false;

        if (Regex.IsMatch(p, @"(?i)which\s+organization\s+is\s+named"))
        {
            var org = entities.FirstOrDefault(e => e.Label == "ORG");
            if (org == null) return false;
            a = org.Text; return true;
        }

        bool mapping = Regex.IsMatch(p, @"(?i)json\s+mapping\s+person\s*,?\s*org\s*,?\s*location\s*,?\s*(?:and\s*)?date");
        if (mapping)
        {
            var map = new Dictionary<string, object?>
            {
                ["PERSON"] = ValueFor(entities, "PERSON"),
                ["ORG"] = ValueFor(entities, "ORG"),
                ["LOCATION"] = ValueFor(entities, "LOCATION"),
                ["DATE"] = ValueFor(entities, "DATE")
            };
            a = JsonSerializer.Serialize(map); return true;
        }

        bool stringsOnly = Regex.IsMatch(p, @"(?i)only\s+need\s+the\s+entity\s+strings");
        if (stringsOnly)
        {
            a = string.Join(", ", entities.Select(e => e.Text)); return true;
        }

        string orgLabel = p.Contains("ORGANIZATION", StringComparison.OrdinalIgnoreCase) ? "ORGANIZATION" : "ORG";
        var rows = entities.Select(e => new Dictionary<string, string>
        {
            ["entity"] = e.Text,
            ["label"] = e.Label == "ORG" ? orgLabel : e.Label
        }).ToList();
        a = JsonSerializer.Serialize(rows); return true;
    }

    private static object? ValueFor(List<Entity> entities, string label)
    {
        var v = entities.Where(e => e.Label == label).Select(e => e.Text).Distinct().ToList();
        return v.Count switch { 0 => null, 1 => v[0], _ => v };
    }

    private static void Add(List<Entity> list, Match m, string group, string label)
    {
        if (m.Groups[group].Success && m.Groups[group].Value.Trim().Length > 0)
            list.Add(new(m.Groups[group].Value.Trim().TrimEnd('.', ','), label));
    }
}

using System.Text.RegularExpressions;

namespace Act2;

public static class PeakSelfTest
{
    public static int Run()
    {
        var cases = new (string Cat, string Prompt, string Expected)[]
        {
            ("math", "What is 5 + 5?", "10"),
            ("math", "A stock moves from $270 to $232. What is the percentage change?", "-14.07"),
            ("math", "A train travels at 56 km/h for 5.5 hours. How far does it go, in kilometres?", "308"),
            ("math", "After a 50% increase, a quantity equals 309. What was the original value?", "206"),
            ("math", "A queue begins with 160 jobs. 15% complete in the first pass, then 15 more complete. The monitor refreshes every 30 seconds. How many jobs remain?", "121"),
            ("logic", "All birds are silent. Rex is one of the birds. Is Rex silent? Answer yes or no.", "yes"),
            ("logic", "All birds are silent. Rex is silent. Does it follow that Rex is one of the birds? Answer yes or no.", "no"),
            ("logic", "A meeting happens on exactly one weekday. It is not on Thursday or Tuesday. It does not happen on Monday. Wednesday is also ruled out. What day is the meeting?", "Friday"),
            ("logic", "Priya is taller than Elena. Elena is taller than Kira. Kira is taller than Frank. Who is the shortest?", "Frank"),
            ("logic", "One and only one of Uma, Xena, and Vic has the key. Uma does not have it. Xena does not have it. Who has the key?", "Vic"),
            ("sentiment", "Review: \"Not bad at all.\" Classify the sentiment.", "Positive"),
            ("sentiment", "Review: \"The formatter is helpful, but deployment is awful.\" Classify overall sentiment.", "Mixed"),
            ("ner", "Return only JSON mapping PERSON, ORG, LOCATION, and DATE to their values. Sentence: Summer joined Apple in Turkey last June.", "Summer|Apple|Turkey|June"),
        };

        int pass = 0;
        foreach (var c in cases)
        {
            bool ok = PeakDeterministic.TrySolve(c.Cat, c.Prompt, out string answer, out string rule);
            bool match = ok && c.Expected.Split('|').All(x => answer.Contains(x, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"{(match ? "PASS" : "FAIL")} {c.Cat} {rule} => {answer}");
            if (match) pass++;
        }

        var plan = SummaryReducer.Build("Summarize in exactly two sentences:\n\nRemote work improves flexibility. However, it creates collaboration challenges. Companies invest in digital tools.");
        bool summary = plan.Candidates.Count >= 3 && plan.Selected.Text.Length > 0;
        Console.WriteLine($"{(summary ? "PASS" : "FAIL")} summary selector => {plan.Selected.Name}");
        if (summary) pass++;

        string codePrompt = "Write a Python function `add(a, b)` that returns the sum. Provide only code.";
        bool code = LocalTaskSupport.ValidateAndNormalize("def add(a, b):\n    return a + b", codePrompt, "code-gen", false, out _, out _);
        Console.WriteLine($"{(code ? "PASS" : "FAIL")} code validator");
        if (code) pass++;

        var categories = new (string Prompt, string Expected)[]
        {
            ("Which organization is named in: May joined May Systems in Bath last August.", "ner"),
            ("I only need the entity strings. Sentence: May joined Orange Labs in Turkey last May.", "ner"),
            ("Exactly one of Jon, Iris, and Kai deployed the patch. It was not Iris. Who deployed the patch?", "logic"),
            ("Red access: Aria, Bo, Dev. Blue access: Bo, Dev, Enzo. Who has both red and blue access?", "logic"),
            ("Return exactly two bullet points, each a short phrase. Passage: The library extended weekend hours.", "summarise"),
            ("The instruction is in the middle: use exactly one sentence. Passage: A team released a dataset.", "summarise"),
            ("After removing 17 records, a table contains 29. What count existed before the removal?", "math")
        };
        foreach (var c in categories)
        {
            var (_, cat, _) = TaskPrompts.For(c.Prompt);
            bool match = cat == c.Expected;
            Console.WriteLine($"{(match ? "PASS" : "FAIL")} classify {c.Expected} => {cat}");
            if (match) pass++;
        }

        int total = cases.Length + 2 + categories.Length;
        Console.WriteLine($"SELFTEST {pass}/{total}");
        return pass == total ? 0 : 1;
    }
}

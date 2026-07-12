// ── PoolGen: large synthetic task pools with computed ground truth ────────────
//
//   gen        --count N --out <tasks.json> --gold <gold.json> [--seed S] [--cats a,b,c]
//   gen-judge  --results <results.json> --gold <gold.json> [--python python]
//
//   Purpose: stress classify -> solve -> comply on task SHAPES never seen in dev.
//   Every generator randomizes both content and instruction phrasing.
//
//   Categories: math, logic, fact, ner, sent, summ, debug, code
//   Gold modes:
//     numeric   — extract last number from answer, compare within tolerance
//     contains  — answer must contain gold token(s) (case-insensitive; any-of)
//     allof     — answer must contain ALL gold tokens (case-insensitive)
//     py_test   — extract code block, run with appended asserts via python
//     struct    — structural compliance (word/sentence/bullet limits), non-empty
//
//   Judge prints a failure census: blanks, per-category pass/fail, failed ids.
//
//   Wire-up in Program.cs:
//     if (args.Length > 0 && args[0] == "gen")       return PoolGen.RunGen(args.Skip(1).ToArray());
//     if (args.Length > 0 && args[0] == "gen-judge") return PoolGen.RunJudge(args.Skip(1).ToArray());
// ──────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public static class PoolGen
{
    // ── records ───────────────────────────────────────────────────────────────
    public sealed record TaskRow(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("prompt")] string Prompt);

    public sealed record GoldRow(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("gold")] List<string> Gold,
        [property: JsonPropertyName("tolerance")] double Tolerance = 0.01,
        [property: JsonPropertyName("max_words")] int MaxWords = 0,
        [property: JsonPropertyName("min_words")] int MinWords = 0,
        [property: JsonPropertyName("bullets")] int Bullets = 0,
        [property: JsonPropertyName("sentences")] int Sentences = 0,
        [property: JsonPropertyName("tests")] string Tests = "");

    public sealed record ResultRow(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("answer")] string Answer);

    static readonly JsonSerializerOptions JOpt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ── gen entry ─────────────────────────────────────────────────────────────
    public static int RunGen(string[] args)
    {
        int count = 200, seed = Environment.TickCount;
        string outPath = "pool-tasks.json", goldPath = "pool-gold.json";
        string[] cats = { "math", "logic", "fact", "ner", "sent", "summ", "debug", "code" };

        for (int i = 0; i < args.Length; i++)
        {
            string NV() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--count": int.TryParse(NV(), out count); break;
                case "--seed": int.TryParse(NV(), out seed); break;
                case "--out": outPath = NV(); break;
                case "--gold": goldPath = NV(); break;
                case "--cats": cats = NV().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                default: Console.Error.WriteLine($"gen: unknown arg {args[i]}"); return 1;
            }
        }

        var rng = new Random(seed);
        var tasks = new List<TaskRow>();
        var gold = new List<GoldRow>();
        var counters = new Dictionary<string, int>();

        string Id(string cat)
        {
            counters.TryGetValue(cat, out int n);
            counters[cat] = ++n;
            return $"{cat}-g{n}";
        }

        int perCat = Math.Max(1, count / cats.Length);
        foreach (var cat in cats)
        {
            for (int k = 0; k < perCat; k++)
            {
                (string prompt, GoldRow g) = cat switch
                {
                    "math"  => GenMath(rng, Id(cat)),
                    "logic" => GenLogic(rng, Id(cat)),
                    "fact"  => GenFact(rng, Id(cat)),
                    "ner"   => GenNer(rng, Id(cat)),
                    "sent"  => GenSent(rng, Id(cat)),
                    "summ"  => GenSumm(rng, Id(cat)),
                    "debug" => GenDebug(rng, Id(cat)),
                    "code"  => GenCode(rng, Id(cat)),
                    _ => throw new ArgumentException($"unknown category {cat}")
                };
                tasks.Add(new TaskRow(g.TaskId, prompt));
                gold.Add(g);
            }
        }

        File.WriteAllText(outPath, JsonSerializer.Serialize(tasks, JOpt));
        File.WriteAllText(goldPath, JsonSerializer.Serialize(gold, JOpt));
        Console.Error.WriteLine($"gen: {tasks.Count} tasks (seed={seed}) -> {outPath}, gold -> {goldPath}");
        foreach (var kv in counters) Console.Error.WriteLine($"  {kv.Key,-6} {kv.Value}");
        return 0;
    }

    // ── phrasing pools ────────────────────────────────────────────────────────
    static T Pick<T>(Random r, IReadOnlyList<T> xs) => xs[r.Next(xs.Count)];

    static readonly string[] NumberAskStyles =
    {
        "Answer with just the number.",
        "Provide only the final numeric answer.",
        "Show your work, then state the final answer on its own line.",
        "Give the final answer. Keep it brief.",
        "What is the result? Respond with the number only.",
        "" // no instruction at all — classifier must cope
    };

    static readonly string[] Names = { "Alice", "Ben", "Carla", "Diego", "Elena", "Frank", "Grace", "Hiro", "Ines", "Jamal", "Kira", "Liam", "Maya", "Noah", "Priya", "Quinn" };
    static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" };

    // ── math ──────────────────────────────────────────────────────────────────
    static (string, GoldRow) GenMath(Random r, string id)
    {
        int kind = r.Next(5);
        string prompt; double ans;
        switch (kind)
        {
            case 0: // discount + tax
            {
                double price = Math.Round(r.Next(40, 900) + r.Next(0, 100) / 100.0, 2);
                int disc = Pick(r, new[] { 10, 15, 20, 25, 30, 40 });
                double taxPct = Pick(r, new[] { 5.0, 6.5, 7.25, 8.0, 8.75, 9.5 });
                ans = Math.Round(price * (1 - disc / 100.0) * (1 + taxPct / 100.0), 2);
                string name = Pick(r, Names);
                prompt = Pick(r, new[]
                {
                    $"A jacket costs ${price:F2}. It's on sale for {disc}% off, and sales tax is {taxPct}%. What is the final price? {Pick(r, NumberAskStyles)}",
                    $"{name} buys an item priced at ${price:F2} with a {disc}% discount applied at the register. Tax of {taxPct}% is added after the discount. How much does {name} pay in total? {Pick(r, NumberAskStyles)}",
                    $"Original price: ${price:F2}. Discount: {disc}%. Sales tax: {taxPct}% (applied after discount). Compute the amount due. {Pick(r, NumberAskStyles)}"
                });
                break;
            }
            case 1: // compound interest
            {
                int p = r.Next(1, 20) * 500;
                double rate = Pick(r, new[] { 3.0, 4.0, 4.5, 5.0, 6.0, 7.0 });
                int n = Pick(r, new[] { 1, 4, 12 });
                int t = r.Next(2, 11);
                ans = Math.Round(p * Math.Pow(1 + rate / 100.0 / n, n * t), 2);
                string freq = n == 1 ? "annually" : n == 4 ? "quarterly" : "monthly";
                prompt = Pick(r, new[]
                {
                    $"${p} is invested at {rate}% annual interest, compounded {freq}, for {t} years. What is the final amount? {Pick(r, NumberAskStyles)}",
                    $"Calculate the value of a ${p} deposit after {t} years at {rate}% APR compounded {freq}. {Pick(r, NumberAskStyles)}"
                });
                break;
            }
            case 2: // rate/distance
            {
                int speed = r.Next(30, 90);
                double hours = r.Next(2, 9) + Pick(r, new[] { 0.0, 0.5 });
                ans = Math.Round(speed * hours, 2);
                string name = Pick(r, Names);
                prompt = Pick(r, new[]
                {
                    $"A train travels at {speed} km/h for {hours} hours. How far does it go, in kilometres? {Pick(r, NumberAskStyles)}",
                    $"{name} drives at a constant {speed} mph for {hours} hours. What distance is covered in miles? {Pick(r, NumberAskStyles)}"
                });
                break;
            }
            case 3: // percent change
            {
                int a = r.Next(40, 400), b = a + r.Next(10, 200);
                bool up = r.Next(2) == 0;
                if (!up) (a, b) = (b, a);
                ans = Math.Round((b - a) * 100.0 / a, 2);
                prompt = Pick(r, new[]
                {
                    $"A stock moves from ${a} to ${b}. What is the percentage change? {Pick(r, NumberAskStyles)}",
                    $"Monthly active users went from {a} thousand to {b} thousand. Compute the percent change. {Pick(r, NumberAskStyles)}"
                });
                break;
            }
            default: // work backwards: after X% increase result is Y, find original
            {
                int pct = Pick(r, new[] { 10, 20, 25, 50 });
                int orig = r.Next(20, 200) * 2;
                double res = orig * (1 + pct / 100.0);
                ans = orig;
                prompt = $"After a {pct}% increase, a quantity equals {res:0.##}. What was the original value? {Pick(r, NumberAskStyles)}";
                break;
            }
        }
        return (prompt, new GoldRow(id, "math", "numeric", new List<string> { ans.ToString(CultureInfo.InvariantCulture) }, Tolerance: 0.02));
    }

    // ── logic ─────────────────────────────────────────────────────────────────
    static (string, GoldRow) GenLogic(Random r, string id)
    {
        int kind = r.Next(3);
        switch (kind)
        {
            case 0: // transitive ordering
            {
                var people = Names.OrderBy(_ => r.Next()).Take(4).ToArray(); // people[0] tallest .. people[3] shortest
                var clues = new List<string>
                {
                    $"{people[0]} is taller than {people[1]}.",
                    $"{people[1]} is taller than {people[2]}.",
                    $"{people[2]} is taller than {people[3]}."
                };
                clues = clues.OrderBy(_ => r.Next()).ToList();
                bool askTall = r.Next(2) == 0;
                string gold = askTall ? people[0] : people[3];
                string q = askTall ? "Who is the tallest?" : "Who is the shortest?";
                string style = Pick(r, new[]
                {
                    $"{string.Join(" ", clues)} {q}",
                    $"Given the following facts:\n- {string.Join("\n- ", clues)}\n{q} Answer with just the name.",
                    $"Solve this: {string.Join(" ", clues)} {q} Explain briefly, then give the name."
                });
                return (style, new GoldRow(id, "logic", "contains", new List<string> { gold }));
            }
            case 1: // day elimination
            {
                string gold = Pick(r, Days);
                var others = Days.Where(d => d != gold).OrderBy(_ => r.Next()).ToArray();
                var clues = new List<string>
                {
                    $"It is not on {others[0]} or {others[1]}.",
                    $"It does not happen on {others[2]}.",
                    $"{others[3]} is also ruled out."
                };
                string prompt = Pick(r, new[]
                {
                    $"A meeting happens on exactly one weekday. {string.Join(" ", clues)} What day is the meeting? Answer with the day only.",
                    $"One of Monday-Friday is the delivery day. {string.Join(" ", clues)} Which day is it?"
                });
                return (prompt, new GoldRow(id, "logic", "contains", new List<string> { gold }));
            }
            default: // syllogism yes/no
            {
                string[] a = { "cats", "robots", "planets", "novels", "birds" };
                string[] b = { "fast", "heavy", "silent", "ancient", "bright" };
                string x = Pick(r, a); string y = Pick(r, b); string name = Pick(r, Names);
                bool valid = r.Next(2) == 0;
                string prompt = valid
                    ? $"All {x} are {y}. Rex is one of the {x}. Is Rex {y}? Answer yes or no."
                    : $"All {x} are {y}. Rex is {y}. Does it follow that Rex is one of the {x}? Answer yes or no.";
                string gold = valid ? "yes" : "no";
                return (prompt, new GoldRow(id, "logic", "contains", new List<string> { gold }));
            }
        }
    }

    // ── fact ──────────────────────────────────────────────────────────────────
    static readonly (string Q, string A)[] Facts =
    {
        ("What is the capital of France?", "Paris"),
        ("What is the capital of Japan?", "Tokyo"),
        ("What is the capital of Australia?", "Canberra"),
        ("What is the capital of Canada?", "Ottawa"),
        ("What is the chemical symbol for gold?", "Au"),
        ("What is the chemical symbol for iron?", "Fe"),
        ("What planet is known as the Red Planet?", "Mars"),
        ("What is the largest planet in the solar system?", "Jupiter"),
        ("How many sides does a hexagon have?", "6"),
        ("What is the freezing point of water in Celsius?", "0"),
        ("What gas do plants primarily absorb for photosynthesis?", "carbon dioxide"),
        ("What is the longest river in South America?", "Amazon"),
        ("In what year did the Apollo 11 moon landing occur?", "1969"),
        ("What is the smallest prime number?", "2"),
        ("How many continents are there?", "7"),
        ("What is the hardest natural substance?", "diamond"),
    };

    static (string, GoldRow) GenFact(Random r, string id)
    {
        var (q, a) = Pick(r, Facts);
        string prompt = Pick(r, new[]
        {
            q,
            $"{q} Answer in one word or number if possible.",
            $"Quick question: {q.ToLowerInvariant()}",
            $"{q} Provide a brief, direct answer."
        });
        return (prompt, new GoldRow(id, "fact", "contains", new List<string> { a }));
    }

    // ── ner ───────────────────────────────────────────────────────────────────
    static readonly string[] Orgs = { "Microsoft", "Toyota", "Siemens", "the World Health Organization", "Stanford University", "Samsung", "the European Space Agency", "Pfizer" };
    static readonly string[] Places = { "Berlin", "Nairobi", "Osaka", "Toronto", "São Paulo", "Mumbai", "Oslo", "Cairo" };
    static readonly string[] DatesPool = { "March 3, 2023", "July 2021", "January 15, 2025", "October 2019", "August 8, 2024", "2018" };

    static (string, GoldRow) GenNer(Random r, string id)
    {
        string person = Pick(r, Names) + " " + Pick(r, new[] { "Tanaka", "Okafor", "Silva", "Novak", "Haddad", "Larsen" });
        string org = Pick(r, Orgs);
        string place = Pick(r, Places);
        string date = Pick(r, DatesPool);
        string orgClean = org.StartsWith("the ") ? org[4..] : org;

        string sentence = Pick(r, new[]
        {
            $"On {date}, {person} announced that {org} would open a new facility in {place}.",
            $"{person}, a senior researcher at {org}, presented findings in {place} in {date}.",
            $"{org} confirmed on {date} that {person} will lead its {place} office."
        });
        string instr = Pick(r, new[]
        {
            "Extract all named entities (PERSON, ORG, LOCATION, DATE) from the sentence.",
            "List every named entity in this sentence with its type as JSON.",
            "Identify the people, organizations, locations, and dates mentioned:",
            "Perform named entity recognition on the following text."
        });
        string prompt = $"{instr}\n\"{sentence}\"";
        return (prompt, new GoldRow(id, "ner", "allof", new List<string> { person, orgClean, place, date }));
    }

    // ── sent ──────────────────────────────────────────────────────────────────
    static readonly string[] PosFrag = { "the battery lasts all day", "setup took two minutes", "support resolved my issue immediately", "the build quality feels premium", "it exceeded my expectations" };
    static readonly string[] NegFrag = { "the screen scratched within a week", "it crashes constantly", "shipping took a month", "the manual is useless", "it stopped working after ten days" };
    static readonly string[] NeuFrag = { "the package arrived on Tuesday", "it comes in three colors", "the model number is printed on the base", "it weighs about two pounds" };

    static (string, GoldRow) GenSent(Random r, string id)
    {
        int kind = r.Next(4);
        string review, gold;
        switch (kind)
        {
            case 0:
                review = $"{Capitalize(Pick(r, PosFrag))}, and {Pick(r, PosFrag)}. Would buy again.";
                gold = "positive"; break;
            case 1:
                review = $"{Capitalize(Pick(r, NegFrag))}. {Capitalize(Pick(r, NegFrag))}. Very disappointed.";
                gold = "negative"; break;
            case 2: // mixed, negative-dominant
                review = $"{Capitalize(Pick(r, PosFrag))}, but {Pick(r, NegFrag)} and {Pick(r, NegFrag)}. I regret this purchase.";
                gold = "negative"; break;
            default:
                review = $"{Capitalize(Pick(r, NeuFrag))}. {Capitalize(Pick(r, NeuFrag))}.";
                gold = "neutral"; break;
        }
        string instr = Pick(r, new[]
        {
            "Classify the sentiment of this review as positive, negative, or neutral.",
            "What is the sentiment of the following review? Answer with one word.",
            "Sentiment analysis — label this text (positive/negative/neutral) and justify briefly:",
            "Is this review positive, negative, or neutral?"
        });
        return ($"{instr}\n\"{review}\"", new GoldRow(id, "sent", "contains", new List<string> { gold }));
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── summ ──────────────────────────────────────────────────────────────────
    static readonly string[] SummTopics =
    {
        "The company reported quarterly revenue of $2.1 billion, up 14% year over year. Growth was driven by cloud services, which now account for over half of total sales. Operating margins improved due to workforce reductions completed last spring. Executives cautioned that currency headwinds could slow growth next quarter. The board approved an expanded share buyback program.",
        "Researchers tracked 4,000 participants over ten years to study the effects of moderate exercise on cardiovascular health. Those who walked at least 30 minutes daily showed a 23% lower incidence of heart disease. The effect held across age groups and was strongest in participants over 60. Diet was controlled for in the analysis. The study's authors recommend integrating walking into daily routines.",
        "The city council approved a plan to convert three downtown parking lots into mixed-use developments. Each site will include ground-floor retail, at least 200 residential units, and public green space. Construction begins next year and is expected to take three years. Critics argue the plan removes needed parking; supporters say it addresses the housing shortage. Funding combines municipal bonds and private investment.",
        "A new battery chemistry using sodium instead of lithium has reached commercial pilot production. Sodium is far more abundant and cheaper to source, though the batteries store roughly 30% less energy by weight. Early applications target grid storage, where weight matters less than cost. Two major manufacturers have announced sodium-based product lines. Analysts expect the technology to complement rather than replace lithium."
    };

    static (string, GoldRow) GenSumm(Random r, string id)
    {
        string text = Pick(r, SummTopics);
        int kind = r.Next(3);
        switch (kind)
        {
            case 0:
            {
                string prompt = $"Summarize the following in exactly one sentence:\n{text}";
                return (prompt, new GoldRow(id, "summ", "struct", new List<string>(), Sentences: 1, MinWords: 5));
            }
            case 1:
            {
                int b = r.Next(2, 4);
                string prompt = $"Summarize the key points of this passage in exactly {b} bullet points:\n{text}";
                return (prompt, new GoldRow(id, "summ", "struct", new List<string>(), Bullets: b));
            }
            default:
            {
                int w = Pick(r, new[] { 20, 25, 30, 40 });
                string prompt = $"Summarize this in {w} words or fewer:\n{text}";
                return (prompt, new GoldRow(id, "summ", "struct", new List<string>(), MaxWords: w, MinWords: 5));
            }
        }
    }

    // ── debug ─────────────────────────────────────────────────────────────────
    // Correct function + tests; one bug injected. Judge repairs = model's fixed code must pass tests.
    sealed record DebugCase(string Buggy, string Tests, string Lang = "python");

    static readonly DebugCase[] DebugCases =
    {
        // off-by-one in range (misses last element)
        new(
"def sum_list(nums):\n    total = 0\n    for i in range(len(nums) - 1):\n        total += nums[i]\n    return total",
"assert sum_list([1, 2, 3, 4]) == 10\nassert sum_list([]) == 0\nassert sum_list([5]) == 5"),
        // wrong initial value for min
        new(
"def min_of(nums):\n    m = 0\n    for n in nums:\n        if n < m:\n            m = n\n    return m",
"assert min_of([3, 7, 2]) == 2\nassert min_of([5, 9, 6]) == 5\nassert min_of([-1, -5]) == -5"),
        // mutation during iteration
        new(
"def remove_negatives(lst):\n    for x in lst:\n        if x < 0:\n            lst.remove(x)\n    return lst",
"assert remove_negatives([1, -2, -3, 4]) == [1, 4]\nassert remove_negatives([-1, -1, -1]) == []\nassert remove_negatives([2, 3]) == [2, 3]"),
        // wrong comparison direction
        new(
"def count_above(nums, threshold):\n    count = 0\n    for n in nums:\n        if n < threshold:\n            count += 1\n    return count",
"assert count_above([1, 5, 8, 10], 6) == 2\nassert count_above([1, 2], 100) == 0\nassert count_above([7, 7, 7], 6) == 3"),
        // integer division truncation
        new(
"def average(nums):\n    return sum(nums) // len(nums)",
"assert abs(average([1, 2]) - 1.5) < 1e-9\nassert abs(average([2, 4, 6]) - 4.0) < 1e-9"),
        // missing return in branch
        new(
"def fizzbuzz(n):\n    if n % 15 == 0:\n        return 'FizzBuzz'\n    elif n % 3 == 0:\n        return 'Fizz'\n    elif n % 5 == 0:\n        'Buzz'\n    else:\n        return str(n)",
"assert fizzbuzz(15) == 'FizzBuzz'\nassert fizzbuzz(9) == 'Fizz'\nassert fizzbuzz(10) == 'Buzz'\nassert fizzbuzz(7) == '7'"),
        // string reversal skipping first char
        new(
"def reverse_string(s):\n    result = ''\n    for i in range(len(s) - 1, 0, -1):\n        result += s[i]\n    return result",
"assert reverse_string('abc') == 'cba'\nassert reverse_string('a') == 'a'\nassert reverse_string('') == ''"),
        // accumulator reset inside loop
        new(
"def running_max(nums):\n    result = []\n    for n in nums:\n        m = n\n        if n > m:\n            m = n\n        result.append(m)\n    return result",
"assert running_max([1, 3, 2, 5]) == [1, 3, 3, 5]\nassert running_max([4, 1]) == [4, 4]"),
    };

    static (string, GoldRow) GenDebug(Random r, string id)
    {
        var c = Pick(r, DebugCases);
        string instr = Pick(r, new[]
        {
            "This function has a bug. Identify it and provide the corrected code.",
            "Find and fix the bug in the following code. Show the corrected version.",
            "The code below produces wrong results. Explain the bug briefly and give a fixed implementation.",
            "Debug this:"
        });
        string prompt = $"{instr}\n```python\n{c.Buggy}\n```";
        return (prompt, new GoldRow(id, "debug", "py_test", new List<string>(), Tests: c.Tests));
    }

    // ── code ──────────────────────────────────────────────────────────────────
    sealed record CodeCase(string Spec, string Tests);

    static readonly CodeCase[] CodeCases =
    {
        new("Write a Python function `is_palindrome(s)` that returns True if the string s reads the same forwards and backwards, ignoring case.",
"assert is_palindrome('Level') == True\nassert is_palindrome('abc') == False\nassert is_palindrome('') == True"),
        new("Write a Python function `fibonacci(n)` returning a list of the first n Fibonacci numbers starting from 0.",
"assert fibonacci(5) == [0, 1, 1, 2, 3]\nassert fibonacci(1) == [0]\nassert fibonacci(0) == []"),
        new("Write a Python function `char_count(s)` that returns a dict mapping each character in s to how many times it appears.",
"assert char_count('aab') == {'a': 2, 'b': 1}\nassert char_count('') == {}"),
        new("Write a Python function `flatten(lst)` that flattens a list of lists (one level deep) into a single list.",
"assert flatten([[1, 2], [3], []]) == [1, 2, 3]\nassert flatten([]) == []"),
        new("Write a Python function `second_largest(nums)` that returns the second largest distinct value in a list of numbers. Assume at least two distinct values.",
"assert second_largest([3, 1, 4, 4, 2]) == 3\nassert second_largest([10, 20]) == 10"),
        new("Write a Python function `vowel_count(s)` that returns the number of vowels (a, e, i, o, u, case-insensitive) in the string s.",
"assert vowel_count('Hello World') == 3\nassert vowel_count('xyz') == 0"),
        new("Write a Python function `chunk(lst, size)` that splits a list into consecutive chunks of the given size; the last chunk may be smaller.",
"assert chunk([1,2,3,4,5], 2) == [[1,2],[3,4],[5]]\nassert chunk([], 3) == []"),
    };

    static (string, GoldRow) GenCode(Random r, string id)
    {
        var c = Pick(r, CodeCases);
        string suffix = Pick(r, new[] { "", " Provide only the code.", " Include a brief docstring.", " Return the implementation in a code block." });
        return (c.Spec + suffix, new GoldRow(id, "code", "py_test", new List<string>(), Tests: c.Tests));
    }

    // ── judge entry ───────────────────────────────────────────────────────────
    public static int RunJudge(string[] args)
    {
        string resultsPath = "", goldPath = "pool-gold.json", python = "python";
        for (int i = 0; i < args.Length; i++)
        {
            string NV() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--results": resultsPath = NV(); break;
                case "--gold": goldPath = NV(); break;
                case "--python": python = NV(); break;
                default: Console.Error.WriteLine($"gen-judge: unknown arg {args[i]}"); return 1;
            }
        }
        if (!File.Exists(resultsPath)) { Console.Error.WriteLine($"gen-judge: results not found: {resultsPath}"); return 1; }
        if (!File.Exists(goldPath)) { Console.Error.WriteLine($"gen-judge: gold not found: {goldPath}"); return 1; }

        var results = JsonSerializer.Deserialize<List<ResultRow>>(File.ReadAllText(resultsPath)) ?? new();
        var gold = JsonSerializer.Deserialize<List<GoldRow>>(File.ReadAllText(goldPath)) ?? new();
        var byId = results.ToDictionary(x => x.TaskId, x => x.Answer ?? "");

        var catPass = new Dictionary<string, int>();
        var catTotal = new Dictionary<string, int>();
        var blanks = new List<string>();
        var fails = new List<(string Id, string Cat, string Why)>();

        foreach (var g in gold)
        {
            catTotal.TryGetValue(g.Category, out int t); catTotal[g.Category] = t + 1;
            byId.TryGetValue(g.TaskId, out string? ans); ans ??= "";

            if (string.IsNullOrWhiteSpace(ans))
            {
                blanks.Add(g.TaskId);
                fails.Add((g.TaskId, g.Category, "BLANK"));
                continue;
            }

            (bool ok, string why) = g.Mode switch
            {
                "numeric" => JudgeNumeric(ans, g),
                "contains" => JudgeContains(ans, g, requireAll: false),
                "allof" => JudgeContains(ans, g, requireAll: true),
                "struct" => JudgeStruct(ans, g),
                "py_test" => JudgePy(ans, g, python),
                _ => (false, $"unknown mode {g.Mode}")
            };

            if (ok) { catPass.TryGetValue(g.Category, out int p); catPass[g.Category] = p + 1; }
            else fails.Add((g.TaskId, g.Category, why));
        }

        int totalPass = catPass.Values.Sum(), total = gold.Count;
        Console.WriteLine($"gen-judge: {totalPass}/{total} ({100.0 * totalPass / Math.Max(1, total):F1}%)  blanks={blanks.Count}");
        Console.WriteLine();
        Console.WriteLine("  category census:");
        foreach (var cat in catTotal.Keys.OrderBy(x => x))
        {
            catPass.TryGetValue(cat, out int p);
            Console.WriteLine($"    {cat,-6} {p,3}/{catTotal[cat],-3}");
        }
        if (fails.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  failures:");
            foreach (var (fid, cat, why) in fails)
                Console.WriteLine($"    {fid,-12} [{cat}] {Trunc(why, 110)}");
        }
        return 0;
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ── judges ────────────────────────────────────────────────────────────────
    static (bool, string) JudgeNumeric(string ans, GoldRow g)
    {
        double goldVal = double.Parse(g.Gold[0], CultureInfo.InvariantCulture);
        // strip currency/commas, find all numbers, compare last (final-answer convention)
        var m = Regex.Matches(ans.Replace(",", ""), @"-?\d+(?:\.\d+)?");
        if (m.Count == 0) return (false, "no number found in answer");
        // accept if ANY number matches — models often restate; last-only is too strict
        foreach (Match x in m)
            if (double.TryParse(x.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && Math.Abs(v - goldVal) <= Math.Max(g.Tolerance, Math.Abs(goldVal) * 0.001))
                return (true, "");
        double last = double.Parse(m[^1].Value, CultureInfo.InvariantCulture);
        return (false, $"expected {goldVal}, numbers found ended with {last}");
    }

    static (bool, string) JudgeContains(string ans, GoldRow g, bool requireAll)
    {
        string low = ans.ToLowerInvariant();
        var missing = g.Gold.Where(t => !low.Contains(t.ToLowerInvariant())).ToList();
        if (requireAll)
            return missing.Count == 0 ? (true, "") : (false, $"missing: {string.Join(", ", missing)}");
        return missing.Count < g.Gold.Count ? (true, "") : (false, $"none of [{string.Join(", ", g.Gold)}] present");
    }

    static (bool, string) JudgeStruct(string ans, GoldRow g)
    {
        string t = ans.Trim();
        int words = Regex.Matches(t, @"\S+").Count;
        if (g.MinWords > 0 && words < g.MinWords) return (false, $"too short: {words} words");
        if (g.MaxWords > 0 && words > g.MaxWords) return (false, $"over limit: {words} words > {g.MaxWords}");
        if (g.Sentences > 0)
        {
            int s = Regex.Matches(t, @"[.!?](\s|$)").Count;
            if (s > g.Sentences) return (false, $"expected {g.Sentences} sentence(s), found ~{s}");
        }
        if (g.Bullets > 0)
        {
            int b = Regex.Matches(t, @"(?m)^\s*(?:[-*•]|\d+[.)])\s+").Count;
            if (b != g.Bullets) return (false, $"expected {g.Bullets} bullets, found {b}");
        }
        return (true, "");
    }

    static (bool, string) JudgePy(string ans, GoldRow g, string python)
    {
        string code = ExtractCode(ans);
        if (string.IsNullOrWhiteSpace(code)) return (false, "no code found in answer");

        string tmp = Path.Combine(Path.GetTempPath(), $"poolgen_{Guid.NewGuid():N}.py");
        try
        {
            File.WriteAllText(tmp, code + "\n\n" + g.Tests + "\nprint('PASS')\n");
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{tmp}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "python failed to start");
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return (false, "timeout (10s)"); }
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (p.ExitCode == 0 && stdout.Contains("PASS")) return (true, "");
            string firstErr = stderr.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "test failed";
            return (false, firstErr.Trim());
        }
        catch (Exception ex) { return (false, $"exec error: {ex.Message}"); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // Pull python code out of an answer: prefer fenced blocks, else heuristic def-scan.
    static string ExtractCode(string ans)
    {
        var fences = Regex.Matches(ans, @"```(?:python|py)?\s*\n(.*?)```", RegexOptions.Singleline);
        if (fences.Count > 0)
        {
            // last fenced block containing a def wins (corrected version convention);
            // else last fenced block outright
            var withDef = fences.Cast<Match>().LastOrDefault(m => m.Groups[1].Value.Contains("def "));
            return (withDef ?? fences[^1]).Groups[1].Value;
        }
        // no fences: take from first 'def ' to end
        int idx = ans.IndexOf("def ", StringComparison.Ordinal);
        return idx >= 0 ? ans[idx..] : "";
    }
}

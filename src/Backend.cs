using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Act2;

public interface IEnsembleBackend
{
    Task<string[]> ListModelsAsync(CancellationToken ct);
    Task<ModelOutput> ChatAsync(PromptRow row, string model, string? systemPrompt, int topLogprobs, int maxTokens, double temperature, CancellationToken ct);
    Task<float[]?> EmbedAsync(string text, string embedModel, CancellationToken ct);
    Task UnloadAsync(string model, CancellationToken ct);
}

// Real backend: talks to LM Studio's OpenAI-compatible endpoints (/v1/*) plus the
// LM Studio REST unload (/api/v1/models/unload). Every capture is best-effort:
// logprobs and embeddings are recorded when present and skipped (noted) when not.
public sealed class LmStudioBackend : IEnsembleBackend
{
    private readonly HttpClient _http;
    private readonly string _root;

    public LmStudioBackend(string baseUrl)
    {
        _root = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
    }

    public async Task<string[]> ListModelsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync(_root + "/v1/models", ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
        return ids.ToArray();
    }

    public async Task<ModelOutput> ChatAsync(
        PromptRow row, string model, string? systemPrompt, int topLogprobs, int maxTokens, double temperature, CancellationToken ct)
    {
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(systemPrompt)) messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = row.Prompt });
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };
        if (topLogprobs > 0) { payload["logprobs"] = true; payload["top_logprobs"] = topLogprobs; }

        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await _http.PostAsJsonAsync(_root + "/v1/chat/completions", payload, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            sw.Stop();
            if (!res.IsSuccessStatusCode)
                return Fail(row, model, sw.Elapsed.TotalMilliseconds, $"chat {(int)res.StatusCode}: {Clip(json)}");

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var choice = r.GetProperty("choices")[0];
            string answer = choice.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            string? finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                ? fr.GetString() : null;
            int? pt = null, ctk = null;
            if (r.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var a)) pt = a.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var b)) ctk = b.GetInt32();
            }
            double[]? lps = ExtractLogprobs(choice);
            return new ModelOutput(row.Id, model, answer, finish, pt, ctk, sw.Elapsed.TotalMilliseconds, lps, null, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail(row, model, sw.Elapsed.TotalMilliseconds, "chat exception: " + ex.Message);
        }
    }

    // OpenAI logprobs shape: choices[0].logprobs.content[] each { token, logprob }.
    // Returns the per-token chosen logprob sequence, or null if the server didn't send it.
    private static double[]? ExtractLogprobs(JsonElement choice)
    {
        if (!choice.TryGetProperty("logprobs", out var lp) || lp.ValueKind != JsonValueKind.Object) return null;
        if (!lp.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var o = new List<double>();
        foreach (var tok in content.EnumerateArray())
            if (tok.TryGetProperty("logprob", out var v) && v.ValueKind == JsonValueKind.Number)
                o.Add(v.GetDouble());
        return o.Count > 0 ? o.ToArray() : null;
    }

    public async Task<float[]?> EmbedAsync(string text, string embedModel, CancellationToken ct)
    {
        try
        {
            var payload = new { model = embedModel, input = text };
            using var res = await _http.PostAsJsonAsync(_root + "/v1/embeddings", payload, ct);
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return null;
            var emb = data[0].GetProperty("embedding");
            var o = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (var v in emb.EnumerateArray()) o[i++] = (float)v.GetDouble();
            return o;
        }
        catch { return null; }
    }

    public async Task UnloadAsync(string model, CancellationToken ct)
    {
        try
        {
            var payload = new { model };
            using var _ = await _http.PostAsync(_root + "/api/v1/models/unload",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        }
        catch { /* unload is best-effort; JIT eviction covers us if the route differs by version */ }
    }

    private static ModelOutput Fail(PromptRow row, string model, double ms, string note)
        => new(row.Id, model, "", null, null, null, ms, null, null, note);

    private static string Clip(string s) => s.Length > 200 ? s[..200] + "…" : s;
}

// Deterministic stub for --dry: no HTTP. Produces reproducible pseudo-answers,
// fake logprobs, and fake embeddings so the orchestration + persistence + the
// downstream deviation layer can be exercised end-to-end with no live server.
public sealed class StubBackend : IEnsembleBackend
{
    private readonly string[] _models;
    public StubBackend(string[] models) => _models = models.Length > 0 ? models : new[] { "stub-a", "stub-b", "stub-c" };

    public Task<string[]> ListModelsAsync(CancellationToken ct) => Task.FromResult(_models);

    public Task<ModelOutput> ChatAsync(PromptRow row, string model, string? systemPrompt, int topLogprobs, int maxTokens, double temperature, CancellationToken ct)
    {
        // Seed off (prompt, model) so runs are reproducible. Models sometimes "agree"
        // (same answer) and sometimes diverge, so the deviation layer has real spread.
        // When a constraint system prompt is present, some stub "models" obey (emit a
        // bare token) and some don't (emit chatter) so the compliance report has spread.
        int h = Hash(row.Id + "|" + model);
        int agreeBucket = Hash(row.Id) % 3;                 // 0/1/2 => how many models converge
        string core = (Hash(model) % 3 <= agreeBucket) ? $"ANS_{Hash(row.Id) % 10}" : $"ANS_{h % 10}";
        bool constrained = !string.IsNullOrEmpty(systemPrompt);
        bool obeys = Hash(model) % 4 != 0;                  // ~3/4 of stub models obey rulesets
        string answer = !constrained ? core
            : obeys ? (systemPrompt!.Contains("Y or N") ? (h % 2 == 0 ? "Y" : "N")
                     : systemPrompt.Contains("uppercase letter") ? ((char)('A' + h % 4)).ToString()
                     : systemPrompt.Contains("only the final number") ? (h % 100).ToString()
                     : core)
            : $"Well, I think the answer is {core} because reasons.";
        var lps = new double[3 + h % 5];
        for (int i = 0; i < lps.Length; i++) lps[i] = -((h + i) % 40) / 10.0;   // 0..-3.9
        var emb = new float[8];
        int he = Hash(answer);
        for (int i = 0; i < emb.Length; i++) emb[i] = ((he >> i) & 1) == 1 ? 1f : 0f;
        return Task.FromResult(new ModelOutput(row.Id, model, answer, "stop", 10, lps.Length, 1.0, lps, emb, null));
    }

    public Task<float[]?> EmbedAsync(string text, string embedModel, CancellationToken ct)
    {
        var emb = new float[8]; int h = Hash(text);
        for (int i = 0; i < emb.Length; i++) emb[i] = ((h >> i) & 1) == 1 ? 1f : 0f;
        return Task.FromResult<float[]?>(emb);
    }

    public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;

    private static int Hash(string s)
    {
        unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h & 0x7fffffff; }
    }
}

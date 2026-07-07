using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Act2;

// ── Fireworks AI remote backend ──────────────────────────────────────────────
// The graded remote. OpenAI-compatible chat completions at api.fireworks.ai.
// Same IEnsembleBackend interface as the local side, so the agent loop swaps
// stub -> Fireworks with one constructor change. Key comes from the
// FIREWORKS_API_KEY env var (never a flag, never a file in the repo).
//
// Fireworks model ids look like: accounts/fireworks/models/llama-v3p1-8b-instruct
// Usage fields (prompt_tokens / completion_tokens) are the SCORED currency —
// captured verbatim from the response, never estimated.
public sealed class FireworksBackend : IEnsembleBackend
{
    // Default root; the harness OVERRIDES this via FIREWORKS_BASE_URL — every
    // call must go through the judging proxy or it scores zero tokens and
    // invalidates the run. Never hardcode around this.
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly string? _reasoningEffort;   // "low"|"medium"|"high"|null

    public FireworksBackend(string? apiKey = null, string? reasoningEffort = null, string? baseUrl = null)
    {
        string key = apiKey ?? Environment.GetEnvironmentVariable("FIREWORKS_API_KEY") ?? "";
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("FIREWORKS_API_KEY not set (env var).");
        _root = (baseUrl
            ?? Environment.GetEnvironmentVariable("FIREWORKS_BASE_URL")
            ?? "https://api.fireworks.ai/inference/v1").TrimEnd('/');
        _reasoningEffort = reasoningEffort;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<string[]> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(_root + "/models", ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        ids.Add(id.GetString()!);
            return ids.ToArray();
        }
        catch { return Array.Empty<string>(); }
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
        // reasoning suppression for gpt-oss/reasoning-class models: remote output
        // tokens are the SCORED spend, so remote thinking is directly score-negative.
        if (_reasoningEffort != null) payload["reasoning_effort"] = _reasoningEffort;

        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await _http.PostAsJsonAsync(_root + "/chat/completions", payload, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            sw.Stop();
            if (!res.IsSuccessStatusCode)
                return Fail(row, model, sw.Elapsed.TotalMilliseconds, $"fireworks {(int)res.StatusCode}: {Clip(json)}");

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
            return new ModelOutput(row.Id, model, answer, finish, pt, ctk, sw.Elapsed.TotalMilliseconds, null, null, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail(row, model, sw.Elapsed.TotalMilliseconds, "fireworks exception: " + ex.Message);
        }
    }

    // remote embeddings not needed by the agent (local side embeds); return null
    public Task<float[]?> EmbedAsync(string text, string embedModel, CancellationToken ct)
        => Task.FromResult<float[]?>(null);

    public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;

    private static ModelOutput Fail(PromptRow row, string model, double ms, string note)
        => new(row.Id, model, "", null, null, null, ms, null, null, note);

    private static string Clip(string s) => s.Length > 200 ? s[..200] + "…" : s;
}

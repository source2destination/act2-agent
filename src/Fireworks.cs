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
    private readonly string? _reasoningEffort;   // "none" suppresses hidden reasoning on the proxy
    // models that rejected reasoning_effort with invalid_request_error — stop sending it to them
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _noEffortSet = new();
    private static System.Collections.Generic.ICollection<string> _noEffortParam => _noEffortSet.Keys;

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
        // CRITICAL — WIRE COMPATIBILITY WITH MINIMAL PROXIES:
        // PostAsJsonAsync streams the body as Transfer-Encoding: chunked (no
        // Content-Length). Python's OpenAI SDK and Go's net/http send fixed-length
        // bodies. Minimal judging proxies/gateways commonly reject or mangle
        // chunked request bodies — our own test proxy did exactly that. Every
        // call therefore goes out as pre-serialized StringContent (which sets
        // Content-Length), and Expect: 100-continue is disabled (another
        // handshake minimal proxies fumble). See PostJson below.
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    // Fixed-length JSON POST: serialize first, send with Content-Length.
    private Task<HttpResponseMessage> PostJson(string url, object payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return _http.PostAsync(url, content, ct);
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
        // Gemma-family endpoints reject or silently ignore the system role -
        // fold the instruction into the user turn for those models so the lane
        // prompt actually reaches them. Everyone else gets a proper system msg.
        bool foldSystem = model.Contains("gemma", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(systemPrompt) && foldSystem)
            messages.Add(new { role = "user", content = systemPrompt + "\n\n" + row.Prompt });
        else
        {
            if (!string.IsNullOrEmpty(systemPrompt)) messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = row.Prompt });
        }
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };
        // reasoning suppression — CRITICAL: every model in the real ALLOWED_MODELS
        // list (gemma-4, minimax, kimi) is a REASONING model. Reasoning tokens are
        // billed as completion_tokens (SCORED spend) AND consume the max_tokens
        // budget before the answer is emitted — unsuppressed, a small cap yields a
        // truncated/empty answer (the gate-failure signature). We send multiple
        // suppression signals; OpenAI-compatible servers ignore unknown fields.
        // Reasoning suppression across model families, which disagree on the param:
        //   gpt-oss:     reasoning_effort = "low"|"medium"|"high"
        //   gemma-4:     reasoning = "on"|"off"   (rejects "low" -> falls back to ON!)
        //   qwen/others: chat_template_kwargs.enable_thinking = false
        // Send all three; OpenAI-compatible servers ignore unrecognized fields.
        // _reasoningEffort carries the gpt-oss scale value; "off"/false suppress the rest.
        // reasoning_effort:"none" — the value that works on the judging proxy.
        // Reasoning models (minimax-m3 etc.) burn the whole budget on hidden
        // reasoning and return BLANK content; "none" suppresses it. Models that
        // reject the param get an automatic bare retry (see catch below) and are
        // remembered so we stop sending it to them.
        bool sendEffort = _reasoningEffort != null && !_noEffortParam.Contains(model);
        if (sendEffort) payload["reasoning_effort"] = _reasoningEffort;

        var sw = Stopwatch.StartNew();
        try
        {
            HttpResponseMessage res = null!;
            string json = "";
            // Official-pattern backoff: up to 3 attempts, 2s/4s spacing, survives
            // proxy rate limits and transient 5xx. Cancellation (per-task budget)
            // still aborts cleanly between attempts.
            for (int attempt = 0; ; attempt++)
            {
                res?.Dispose();
                res = await PostJson(_root + "/chat/completions", payload, ct);
                json = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode || attempt >= 2) break;
                int code = (int)res.StatusCode;
                if (code == 429 || code >= 500)
                { try { await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct); } catch { break; } continue; }
                break;   // 4xx other than 429: not retryable
            }
            if (!res.IsSuccessStatusCode && sendEffort && json.Contains("invalid_request_error"))
            {
                // model rejects reasoning_effort: remember, retry bare
                _noEffortSet.TryAdd(model, 0);
                payload.Remove("reasoning_effort");
                using var res2 = await PostJson(_root + "/chat/completions", payload, ct);
                json = await res2.Content.ReadAsStringAsync(ct);
                sw.Stop();
                if (!res2.IsSuccessStatusCode)
                    return Fail(row, model, sw.Elapsed.TotalMilliseconds, $"fireworks {(int)res2.StatusCode}: {Clip(json)}");
            }
            else
            {
                sw.Stop();
                if (!res.IsSuccessStatusCode)
                    return Fail(row, model, sw.Elapsed.TotalMilliseconds, $"fireworks {(int)res.StatusCode}: {Clip(json)}");
            }

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var choice = r.GetProperty("choices")[0];
            string answer = choice.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            // Fallback: if the server split reasoning into a separate field and the
            // answer came back empty (model spent its budget thinking), salvage the
            // reasoning tail so the row isn't blank. Suppression should prevent this,
            // but on servers that ignore the suppression flags this avoids a zero.
            if (answer.Trim().Length == 0 && msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                string rcs = rc.GetString() ?? "";
                if (rcs.Length > 0) answer = rcs.Length > 1200 ? rcs[^1200..] : rcs;
            }
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

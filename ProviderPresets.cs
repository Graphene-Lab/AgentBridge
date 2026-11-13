// ═══════════════════════════════════════════════════════════════════════
//  ProviderPresets — what a new user can choose on the first start.
//
//  A preset is data: name, address, protocol, where the service publishes its
//  model list, where to get a key. The wizard never invents a model name — it
//  asks the service itself through <see cref="FetchModelsAsync"/> — and a preset
//  with an empty KeyUrl is a local server, which needs no key at all.
// ═══════════════════════════════════════════════════════════════════════
using System.Net.Http.Headers;
using System.Text.Json;
using AIOrchestrator;

/// <summary>One AI service offered by the first-run wizard.</summary>
/// <param name="Name">Provider name written to providers.json (and shown in /model).</param>
/// <param name="Label">Human label for the picker.</param>
/// <param name="Protocol">Wire protocol the service speaks.</param>
/// <param name="BaseAddress">Base URL; "{model}" is replaced by the chosen model (Gemini).</param>
/// <param name="EndPoint">Path appended to the base for a chat call ("" when the base carries it).</param>
/// <param name="ModelsUrl">Absolute URL that lists the models the key can use.</param>
/// <param name="SuggestedModel">Prefilled model, empty when only the service can say.</param>
/// <param name="KeyUrl">Where to create an API key; empty = local server, no key.</param>
/// <param name="ContextWindow">Context window in tokens for the suggested model.</param>
/// <param name="Note">One line shown under the picker.</param>
public sealed record ProviderPreset(
    string Name,
    string Label,
    ProviderProtocol Protocol,
    string BaseAddress,
    string EndPoint,
    string ModelsUrl,
    string SuggestedModel,
    string KeyUrl,
    int ContextWindow,
    string Note)
{
    /// <summary>True when the service needs an API key (a local server does not).</summary>
    public bool NeedsKey => KeyUrl.Length > 0;

    /// <summary>The base address for one model — Gemini puts the model in the URL.</summary>
    public string AddressFor(string model) => BaseAddress.Replace("{model}", model);
}

public static class ProviderPresets
{
    /// <summary>Presets in the order a new user should read them: cloud, then local.</summary>
    public static readonly ProviderPreset[] All =
    {
        new("DeepSeek", "DeepSeek", ProviderProtocol.OpenAI,
            "https://api.deepseek.com/", "v1/chat/completions",
            "https://api.deepseek.com/v1/models", "deepseek-flash",
            "https://platform.deepseek.com/api_keys", 1_000_000,
            "Low cost, strong at tool calling."),

        new("OpenAI", "OpenAI (GPT)", ProviderProtocol.OpenAI,
            "https://api.openai.com/", "v1/chat/completions",
            "https://api.openai.com/v1/models", "",
            "https://platform.openai.com/api-keys", 400_000,
            "The reference implementation of tool calling."),

        new("Anthropic", "Anthropic (Claude)", ProviderProtocol.Anthropic,
            "https://api.anthropic.com/", "v1/messages",
            "https://api.anthropic.com/v1/models", "",
            "https://console.anthropic.com/settings/keys", 200_000,
            "Careful reading of long documents."),

        new("Gemini", "Google Gemini", ProviderProtocol.Gemini,
            "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent", "",
            "https://generativelanguage.googleapis.com/v1beta/models", "",
            "https://aistudio.google.com/apikey", 1_000_000,
            "Very large context at a low price."),

        new("Qwen", "Qwen (Alibaba DashScope)", ProviderProtocol.OpenAI,
            "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/", "chat/completions",
            "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/models", "",
            "https://bailian.console.alibabacloud.com/", 128_000,
            "Qwen models through DashScope."),

        new("OpenRouter", "OpenRouter", ProviderProtocol.OpenAI,
            "https://openrouter.ai/api/", "v1/chat/completions",
            "https://openrouter.ai/api/v1/models", "",
            "https://openrouter.ai/keys", 128_000,
            "One key, many vendors' models."),

        new("Mistral", "Mistral AI", ProviderProtocol.OpenAI,
            "https://api.mistral.ai/", "v1/chat/completions",
            "https://api.mistral.ai/v1/models", "",
            "https://console.mistral.ai/api-keys", 128_000,
            "A European provider."),

        new("Ollama", "Ollama (this computer)", ProviderProtocol.OpenAI,
            "http://localhost:11434/", "v1/chat/completions",
            "http://localhost:11434/v1/models", "",
            "", 32_768,
            "Runs on this computer: no key, no cloud."),

        new("LMStudio", "LM Studio (this computer)", ProviderProtocol.OpenAI,
            "http://localhost:1234/", "v1/chat/completions",
            "http://localhost:1234/v1/models", "",
            "", 32_768,
            "A local server you start yourself."),

        new("Other", "Other OpenAI-compatible endpoint", ProviderProtocol.OpenAI,
            "http://localhost:8080/", "v1/chat/completions",
            "http://localhost:8080/v1/models", "",
            "", 32_768,
            "Any address that speaks the OpenAI API."),
    };

    /// <summary>Asks the service which models the key can use, and returns their ids.
    /// Throws <see cref="InvalidOperationException"/> with the service's own message when the
    /// answer is not a success — the wizard shows that text instead of guessing why.</summary>
    public static async Task<string[]> FetchModelsAsync(ProviderPreset preset, string apiKey, CancellationToken ct = default)
    {
        var url = preset.Protocol == ProviderProtocol.Gemini && preset.NeedsKey
            ? preset.ModelsUrl + "?key=" + Uri.EscapeDataString(apiKey)
            : preset.ModelsUrl;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (preset.Protocol == ProviderProtocol.Anthropic)
        {
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (preset.NeedsKey && apiKey.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {ErrorMessage(body)}");

        using var doc = JsonDocument.Parse(body);
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s) ids.Add(s);
        }
        else if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            // Gemini: name is "models/<id>", and only some entries can generate content.
            foreach (var item in models.EnumerateArray())
            {
                if (item.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.ValueKind == JsonValueKind.Array
                    && !methods.EnumerateArray().Any(m => m.GetString() == "generateContent")) continue;
                if (item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                    ids.Add(n.StartsWith("models/", StringComparison.Ordinal) ? n[7..] : n);
            }
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The service's own error text when the body carries one, else the body's head.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg)
                    return msg;
                if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text)
                    return text;
            }
        }
        catch { }
        var head = body.Trim().Replace("\r", " ").Replace("\n", " ");
        return head.Length > 200 ? head[..200] : head;
    }
}

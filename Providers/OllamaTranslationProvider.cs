using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

public class OllamaTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient = new();

    public string Name => "ollama";
    public string DisplayName => $"Ollama ({Model})";

    private string _baseUrl = "http://localhost:11434";
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            _baseUrl = value;
            _httpClient.BaseAddress = new Uri(_baseUrl);
        }
    }
    public string Model { get; set; } = "gemma3:1b";

    private class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;
        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;
    }

    private class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelEntry>? Models { get; set; }
    }

    private class OllamaModelEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default)
    {
        var langNames = new Dictionary<string, string>
        {
            ["ja"] = "Japanese", ["en"] = "English",
            ["zh"] = "Chinese", ["ko"] = "Korean", ["fr"] = "French", ["de"] = "German"
        };

        var srcName = langNames.GetValueOrDefault(sourceLang, sourceLang);
        var tgtName = langNames.GetValueOrDefault(targetLang, targetLang);

        var payload = new OllamaGenerateRequest
        {
            Model = Model,
            Prompt = $"Translate the following {srcName} text to {tgtName}. Only provide the translation, no explanations:\n\n{text}",
            Stream = false
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync("/api/generate", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            return $"Translation Failed: {ex.Message}";
        }

        var result = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(result);
        var translated = doc.RootElement.GetProperty("response").GetString();

        return string.IsNullOrWhiteSpace(translated) ? "Translation Failed" : translated.Trim();
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/api/tags", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(body);
        return tags?.Models?.Select(m => m.Name ?? "unknown").Where(n => n != "unknown").ToList() ?? new List<string>();
    }
}

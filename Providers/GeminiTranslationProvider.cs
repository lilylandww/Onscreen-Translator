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

/// <summary>
/// Translation provider using Google's Gemini generateContent API.
/// Supports both free-tier and paid API keys (x-goog-api-key header).
/// </summary>
public class GeminiTranslationProvider : ITranslationProvider, IModelListable, IDisposable
{
    private HttpClient? _httpClient;
    private bool _disposed;

    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["ja"] = "Japanese", ["en"] = "English",
        ["zh"] = "Chinese", ["ko"] = "Korean",
        ["fr"] = "French", ["de"] = "German",
        ["es"] = "Spanish", ["pt"] = "Portuguese",
        ["ru"] = "Russian", ["it"] = "Italian"
    };

    public string Name => "gemini";
    public string DisplayName => $"Google Gemini ({Model})";

    private string _baseUrl = "https://generativelanguage.googleapis.com/v1beta";
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            _baseUrl = value.TrimEnd('/');
            if (_httpClient != null)
                _httpClient.BaseAddress = new Uri(_baseUrl + "/");
        }
    }

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.0-flash";
    public int TimeoutSeconds { get; set; } = 60;

    public GeminiTranslationProvider()
    {
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            BaseAddress = new Uri(_baseUrl + "/")
        };

        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", ApiKey);
        }

        return _httpClient;
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default)
    {
        var srcName = LanguageNames.GetValueOrDefault(sourceLang, sourceLang);
        var tgtName = LanguageNames.GetValueOrDefault(targetLang, targetLang);

        const string formatPrompt =
            "You are a professional translator. Translate the given text from {0} to {1}. " +
            "Output only the translation, nothing else. Maintain the original tone and nuance.";

        var request = new GeminiRequest
        {
            SystemInstruction = new GeminiContent
            {
                Parts = [new GeminiPart { Text = string.Format(formatPrompt, srcName, tgtName) }]
            },
            Contents =
            [
                new GeminiContent
                {
                    Role = "user",
                    Parts = [new GeminiPart { Text = text }]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig { Temperature = 0.3, MaxOutputTokens = 4096 }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var client = GetClient();
            var response = await client.PostAsync($"models/{Model}:generateContent", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GeminiResponse>(responseBody);
            var translated = result?.Candidates?.FirstOrDefault()?.Content?.Parts?
                .Select(p => p.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                ?.Trim();

            return string.IsNullOrWhiteSpace(translated) ? "Translation Failed" : translated;
        }
        catch (HttpRequestException ex)
        {
            return $"Translation Failed: {ex.Message}";
        }
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("models");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("models", ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            var models = JsonSerializer.Deserialize<GeminiModelsResponse>(body);
            var list = models?.Models?
                .Where(m => m.Name != null
                    && m.SupportedGenerationMethods?.Contains("generateContent") == true)
                .Select(m => m.Name!.StartsWith("models/", StringComparison.Ordinal)
                    ? m.Name["models/".Length..]
                    : m.Name!)
                .Where(n => !n.Contains("embedding", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .ToList() ?? new List<string>();
            return list;
        }
        catch
        {
            return new List<string>();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

#region Gemini API DTOs

internal class GeminiRequest
{
    [JsonPropertyName("systemInstruction")]
    public GeminiContent? SystemInstruction { get; set; }

    [JsonPropertyName("contents")]
    public List<GeminiContent> Contents { get; set; } = new();

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig? GenerationConfig { get; set; }
}

internal class GeminiContent
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("parts")]
    public List<GeminiPart> Parts { get; set; } = new();
}

internal class GeminiPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("inlineData")]
    public GeminiInlineData? InlineData { get; set; }
}

internal class GeminiInlineData
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "image/png";

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}

internal class GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.3;

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; } = 4096;
}

internal class GeminiResponse
{
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }
}

internal class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }
}

internal class GeminiModelsResponse
{
    [JsonPropertyName("models")]
    public List<GeminiModelEntry>? Models { get; set; }
}

internal class GeminiModelEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("supportedGenerationMethods")]
    public List<string>? SupportedGenerationMethods { get; set; }
}

#endregion
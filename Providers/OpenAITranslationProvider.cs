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
/// Translation provider using OpenAI's Chat Completions API.
/// Also works with OpenAI-compatible endpoints.
/// </summary>
public class OpenAITranslationProvider : ITranslationProvider, IModelListable, IDisposable
{
    private HttpClient? _httpClient;
    private bool _disposed;

    public string Name => _providerName;
    public string DisplayName => $"{_displayName} ({Model})";

    private readonly string _providerName;
    private readonly string _displayName;

    private string _baseUrl = "https://api.openai.com/v1";
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            _baseUrl = value;
            if (_httpClient != null)
                _httpClient.BaseAddress = new Uri(_baseUrl);
        }
    }

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 60;

    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["ja"] = "Japanese", ["en"] = "English",
        ["zh"] = "Chinese", ["ko"] = "Korean",
        ["fr"] = "French", ["de"] = "German",
        ["es"] = "Spanish", ["pt"] = "Portuguese",
        ["ru"] = "Russian", ["it"] = "Italian"
    };

    public OpenAITranslationProvider(string providerName = "openai", string displayName = "OpenAI")
    {
        _providerName = providerName;
        _displayName = displayName;
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            BaseAddress = new Uri(_baseUrl)
        };

        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
        }

        return _httpClient;
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default)
    {
        var srcName = LanguageNames.GetValueOrDefault(sourceLang, sourceLang);
        var tgtName = LanguageNames.GetValueOrDefault(targetLang, targetLang);

        var request = new ChatRequest
        {
            Model = Model,
            Messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "system",
                    Content = $"You are a professional translator. Translate the given text from {srcName} to {tgtName}. Output only the translation, nothing else. Maintain the original tone and nuance."
                },
                new()
                {
                    Role = "user",
                    Content = text
                }
            }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var client = GetClient();
            var response = await client.PostAsync("/chat/completions", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<ChatResponse>(responseBody);
            var translated = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

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
            var response = await client.GetAsync("/models");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("/models", ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            var models = JsonSerializer.Deserialize<ModelsResponse>(body);
            return models?.Data?
                .Select(m => m.Id ?? "unknown")
                .Where(n => n != "unknown")
                .OrderBy(n => n)
                .ToList() ?? new List<string>();
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

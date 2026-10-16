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
/// Translation provider using Anthropic's Messages API (Claude models).
/// Auth uses the x-api-key + anthropic-version headers rather than Bearer.
/// </summary>
public class AnthropicTranslationProvider : ITranslationProvider, IModelListable, IDisposable
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

    public string Name => "anthropic";
    public string DisplayName => $"Anthropic Claude ({Model})";

    private string _baseUrl = "https://api.anthropic.com";
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
    public string Model { get; set; } = "claude-haiku-4-5";
    public int TimeoutSeconds { get; set; } = 60;

    private const string AnthropicApiVersion = "2023-06-01";

    public AnthropicTranslationProvider()
    {
    }

    /// <summary>
    /// Normalizes a configured base URL. Accepts either the bare API root
    /// ("https://api.anthropic.com") or a URL that already includes "/v1";
    /// routes are appended relative to the returned value.
    /// </summary>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/v1";
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            BaseAddress = new Uri(NormalizeBaseUrl(_baseUrl) + "/")
        };

        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        }
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", AnthropicApiVersion);

        return _httpClient;
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default)
    {
        var srcName = LanguageNames.GetValueOrDefault(sourceLang, sourceLang);
        var tgtName = LanguageNames.GetValueOrDefault(targetLang, targetLang);

        var request = new AnthropicRequest
        {
            Model = Model,
            MaxTokens = 4096,
            Temperature = 0.3,
            System = $"You are a professional translator. Translate the given text from {srcName} to {tgtName}. Output only the translation, nothing else. Maintain the original tone and nuance.",
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContentBlock { Text = text }]
                }
            ]
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var client = GetClient();
            var response = await client.PostAsync("messages", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<AnthropicResponse>(responseBody);
            var translated = result?.Content?
                .Where(b => b.Type == "text")
                .Select(b => b.Text)
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

#region Anthropic API DTOs

internal class AnthropicRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.3;

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; set; }

    [JsonPropertyName("messages")]
    public List<AnthropicMessage> Messages { get; set; } = new();
}

internal class AnthropicMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<AnthropicContentBlock> Content { get; set; } = new();
}

internal class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicImageSource? Source { get; set; }
}

internal class AnthropicImageSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "base64";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "image/png";

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}

internal class AnthropicResponse
{
    [JsonPropertyName("content")]
    public List<AnthropicContentBlock>? Content { get; set; }
}

#endregion
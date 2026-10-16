using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers.Breakdown;

/// <summary>
/// Sentence-breakdown provider using Google's Gemini generateContent API.
/// Parsing/alignment is delegated to <see cref="BreakdownParser"/>; prompts are
/// shared with the other breakdown providers via <see cref="BreakdownPrompts"/>.
/// Callers pass the Gemini base URL including its version path
/// (e.g. "https://generativelanguage.googleapis.com/v1beta").
/// </summary>
public class GeminiSentenceBreakdownProvider : ISentenceBreakdownProvider, IDisposable
{
    private HttpClient? _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private bool _disposed;

    public GeminiSentenceBreakdownProvider(string baseUrl, string apiKey, string model)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        // Allow the bare API root; default to the v1beta version path otherwise
        if (!trimmed.Contains("/v1", StringComparison.OrdinalIgnoreCase))
            trimmed += "/v1beta";
        _baseUrl = trimmed;
        _apiKey = apiKey;
        _model = model;
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
            BaseAddress = new Uri(_baseUrl + "/")
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);
        }

        return _httpClient;
    }

    public async Task<List<BreakdownPart>> BreakdownAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<BreakdownPart>();

        try
        {
            var request = new GeminiRequest
            {
                SystemInstruction = new GeminiContent
                {
                    Parts = [new GeminiPart { Text = BreakdownPrompts.SystemPrompt }]
                },
                Contents =
                [
                    new GeminiContent
                    {
                        Role = "user",
                        Parts = [new GeminiPart { Text = BreakdownPrompts.BuildUserPrompt(text) }]
                    }
                ],
                GenerationConfig = new GeminiGenerationConfig { Temperature = 0.1, MaxOutputTokens = 2048 }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = GetClient();
            var response = await client.PostAsync($"models/{_model}:generateContent", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GeminiResponse>(responseBody);
            var output = result?.Candidates?.FirstOrDefault()?.Content?.Parts?
                .Select(p => p.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                ?.Trim();

            if (string.IsNullOrWhiteSpace(output))
                return new List<BreakdownPart>();

            return BreakdownParser.Parse(output, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GeminiSentenceBreakdownProvider] Error: {ex.Message}");
            return new List<BreakdownPart>();
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

/// <summary>
/// Sentence-breakdown provider using Anthropic's Messages API. The base URL is
/// normalized the same way as <see cref="AnthropicTranslationProvider"/>
/// (bare API root or a URL already including "/v1").
/// </summary>
public class AnthropicSentenceBreakdownProvider : ISentenceBreakdownProvider, IDisposable
{
    private HttpClient? _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private bool _disposed;

    public AnthropicSentenceBreakdownProvider(string baseUrl, string apiKey, string model)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
            BaseAddress = new Uri(AnthropicTranslationProvider.NormalizeBaseUrl(_baseUrl) + "/")
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        }
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        return _httpClient;
    }

    public async Task<List<BreakdownPart>> BreakdownAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<BreakdownPart>();

        try
        {
            var request = new AnthropicRequest
            {
                Model = _model,
                MaxTokens = 2048,
                Temperature = 0.1,
                System = BreakdownPrompts.SystemPrompt,
                Messages =
                [
                    new AnthropicMessage
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Text = BreakdownPrompts.BuildUserPrompt(text) }]
                    }
                ]
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = GetClient();
            var response = await client.PostAsync("messages", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<AnthropicResponse>(responseBody);
            var output = result?.Content?
                .Where(b => b.Type == "text")
                .Select(b => b.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                ?.Trim();

            if (string.IsNullOrWhiteSpace(output))
                return new List<BreakdownPart>();

            return BreakdownParser.Parse(output, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnthropicSentenceBreakdownProvider] Error: {ex.Message}");
            return new List<BreakdownPart>();
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
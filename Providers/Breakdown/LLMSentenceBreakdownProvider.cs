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
/// Breaks a sentence into vocab + grammar parts via an OpenAI-compatible
/// chat-completions endpoint (Ollama, OpenAI, LM Studio, vLLM, ...).
/// Follows the same request/harness pattern as
/// <c>OllamaFuriganaFallbackProvider</c>; parsing/alignment is delegated to
/// <see cref="BreakdownParser"/>.
/// Callers pass an already-normalized base URL: for Ollama the factory appends
/// <c>/v1</c> (see <c>ProviderFactory.WithOpenAIV1Path</c>), for OpenAI and
/// OpenAI-compatible endpoints the configured URL must already include it.
/// </summary>
public class LLMSentenceBreakdownProvider : ISentenceBreakdownProvider, IDisposable
{
    private HttpClient? _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="LLMSentenceBreakdownProvider"/>.
    /// </summary>
    /// <param name="baseUrl">Base URL of the OpenAI-compatible endpoint including its version path (e.g. "http://localhost:11434/v1", "https://api.openai.com/v1").</param>
    /// <param name="apiKey">API key (may be empty for Ollama).</param>
    /// <param name="model">Model name to use for breakdown generation.</param>
    public LLMSentenceBreakdownProvider(string baseUrl, string apiKey, string model)
    {
        _baseUrl = baseUrl.TrimEnd('/');
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
            BaseAddress = new Uri(_baseUrl)
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        return _httpClient;
    }

    public async Task<List<BreakdownPart>> BreakdownAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<BreakdownPart>();

        try
        {
            var request = new ChatRequest
            {
                Model = _model,
                MaxTokens = 2048,
                Temperature = 0.1,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = BreakdownPrompts.SystemPrompt },
                    new() { Role = "user", Content = BreakdownPrompts.BuildUserPrompt(text) }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = GetClient();
            var response = await client.PostAsync("/chat/completions", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<ChatResponse>(responseBody);
            var output = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

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
            Debug.WriteLine($"[LLMSentenceBreakdownProvider] Error: {ex.Message}");
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers.Furigana;

/// <summary>
/// HTTP client implementation of <see cref="IFuriganaProvider"/> that communicates
/// with the Python FastAPI sidecar service.
/// </summary>
public class HttpFuriganaProvider : IFuriganaProvider, IDisposable
{
    private HttpClient? _httpClient;
    private readonly string _sidecarUrl;
    private bool _disposed;

    public string Name => "sidecar";

    /// <summary>
    /// Creates a new <see cref="HttpFuriganaProvider"/>.
    /// </summary>
    /// <param name="sidecarUrl">Base URL of the sidecar (e.g. "http://127.0.0.1:8765").</param>
    /// <param name="httpClient">Optional pre-configured HttpClient for testing.</param>
    public HttpFuriganaProvider(string sidecarUrl, HttpClient? httpClient = null)
    {
        _sidecarUrl = sidecarUrl.TrimEnd('/');
        _httpClient = httpClient;
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _httpClient ??= new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = new Uri(_sidecarUrl)
        };
    }

    public async Task<List<FuriganaSegment>> GetFuriganaAsync(string text, bool allowFallback, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<FuriganaSegment>();

        try
        {
            var payload = new FuriganaRequest
            {
                Text = text,
                Fallback = allowFallback
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = GetClient();
            var response = await client.PostAsync("/furigana", content, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<FuriganaResponse>(body);
            return result?.Segments ?? new List<FuriganaSegment>();
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HttpFuriganaProvider] Error calling /furigana: {ex.Message}");
            return new List<FuriganaSegment>();
        }
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
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

    // --- Request / Response DTOs matching the sidecar JSON shape ---

    private class FuriganaRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("fallback")]
        public bool Fallback { get; set; } = true;
    }

    private class FuriganaResponse
    {
        [JsonPropertyName("segments")]
        public List<FuriganaSegment>? Segments { get; set; }
    }
}

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
/// Fallback provider that calls the sidecar's <c>/flfl</c> endpoint
/// for out-of-vocabulary kanji readings using the FLFL model.
/// </summary>
public class FlflFallbackProvider : IFuriganaFallbackProvider, IDisposable
{
    private HttpClient? _httpClient;
    private readonly string _sidecarUrl;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="FlflFallbackProvider"/>.
    /// </summary>
    /// <param name="sidecarUrl">Base URL of the sidecar (e.g. "http://127.0.0.1:8765").</param>
    /// <param name="httpClient">Optional pre-configured HttpClient for testing.</param>
    public FlflFallbackProvider(string sidecarUrl, HttpClient? httpClient = null)
    {
        _sidecarUrl = sidecarUrl.TrimEnd('/');
        _httpClient = httpClient;
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _httpClient ??= new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            BaseAddress = new Uri(_sidecarUrl)
        };
    }

    public async Task<List<FuriganaSegment>> GetFallbackAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<FuriganaSegment>();

        try
        {
            var payload = new FlflRequest { Text = text };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = GetClient();
            var response = await client.PostAsync("/flfl", content, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<FlflResponse>(body);
            return result?.Segments ?? new List<FuriganaSegment>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FlflFallbackProvider] Error calling /flfl: {ex.Message}");
            return new List<FuriganaSegment>();
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

    private class FlflRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    private class FlflResponse
    {
        [JsonPropertyName("segments")]
        public List<FuriganaSegment>? Segments { get; set; }
    }
}

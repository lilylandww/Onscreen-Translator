using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnscreenTranslator.Core.Settings;

namespace OnscreenTranslator.Core.Providers;

/// <summary>
/// OCR provider that delegates to a remote server (REST API).
/// The remote server is expected to expose:
///   POST /api/ocr  { imageBase64: "...", model: "..." }  →  { text: "..." }
/// </summary>
public class RemoteOCRProvider : IOCRProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly RemoteServerConfig _config;

    public string Name => "remote";
    public string DisplayName => "Remote Server";

    public RemoteOCRProvider(RemoteServerConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.BaseUrl.TrimEnd('/')) };
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public async Task<string> RecognizeTextAsync(string imagePath, CancellationToken ct = default)
    {
        var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
        var base64 = Convert.ToBase64String(imageBytes);

        var payload = JsonSerializer.Serialize(new { imageBase64 = base64, model = "auto" });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("/api/ocr", content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.GetProperty("text").GetString() ?? "";
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

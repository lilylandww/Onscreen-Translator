using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnscreenTranslator.Core.Settings;

namespace OnscreenTranslator.Core.Providers;

/// <summary>
/// Translation provider that delegates to a remote server (REST API).
/// The remote server is expected to expose:
///   POST /api/translate  { text: "...", sourceLang: "ja", targetLang: "en" }
///   → { translatedText: "..." }
/// </summary>
public class RemoteTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly RemoteServerConfig _config;

    public string Name => "remote";
    public string DisplayName => "Remote Server";

    public RemoteTranslationProvider(RemoteServerConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.BaseUrl.TrimEnd('/')) };
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja",
        string targetLang = "en", CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            text,
            sourceLang,
            targetLang
        });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("/api/translate", content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.GetProperty("translatedText").GetString() ?? "";
    }

    public Task<bool> IsAvailableAsync()
    {
        try
        {
            return Task.FromResult(_http.BaseAddress != null);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public void Dispose() => _http.Dispose();
}

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

public class GoogleTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient _httpClient = new();
    private readonly string? _apiKey;

    public string Name => "google";
    public string DisplayName => "Google Translate";

    public GoogleTranslationProvider(string? apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["source"] = sourceLang,
            ["target"] = targetLang,
            ["format"] = "text"
        };

        var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://translation.googleapis.com/language/translate/v2");
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = content;
        var response = await _httpClient.SendAsync(request, ct);

        var result = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return $"Translation Failed: HTTP {(int)response.StatusCode} - {result}";

        var doc = JsonDocument.Parse(result);
        return doc.RootElement.GetProperty("data")
                   .GetProperty("translations")[0]
                   .GetProperty("translatedText")
                   .GetString() ?? "Translation Failed";
    }

    public Task<bool> IsAvailableAsync() => Task.FromResult(!string.IsNullOrWhiteSpace(_apiKey));
}
